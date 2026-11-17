/// A debug build's probes (see `CoreIr.Probe`), the foundation of the debugger: at each function's start, at each
/// statement, and at each place a function ends, the code calls the runtime, which keeps a shadow stack of the frames
/// from the starts and ends, and stops where it wants to. The lowering puts a probe before each statement of a block
/// (see `CoreIrLower.FunctionLowering.Block`) and around each function's body (`around`).
///
/// A probe uses none of its locals, so it never keeps a value alive longer than the code does, and a debug build frees
/// and reuses memory as a release build does. The emitter writes a local only where it is still alive (see
/// `CoreIrEmit`).
module internal Transpiler.Wasm.CoreIrProbes

open Transpiler.Wasm.CoreIr

/// The probe of `kind`, writing `locals`, at `place`.
let probe (kind: ProbeKind) (locals: ProbeLocal list) (place: Place option) : Expr =
    { Kind = Probe(kind, locals); Type = IrUnit; Place = place }

/// `body`, the function `name`'s, with an entry probe writing `locals` at its start, at `place`, and a return probe at
/// each place it ends, at the place of what it ends in. A function that ends in a call ends before the call, so that a
/// tail call stays one, and the callee's entry follows. One that ends in another value ends once it has the value, in a
/// local `newLocal` makes, so that a loop or a crash at the end is still inside the function's frame.
let around (newLocal: IrType -> Var) (name: string) (place: Place option) (locals: ProbeLocal list) (body: Expr) : Expr =
    let rec ending (e: Expr) =
        let links, last = Chain.split e
        let last =
            match last.Kind with
            | If(condition, ifTrue, ifFalse) -> { last with Kind = If(condition, ending ifTrue, ending ifFalse) }
            | Match(scrutinees, arms) -> { last with Kind = Match(scrutinees, arms |> List.map (fun arm -> { arm with Body = ending arm.Body })) }
            | Call _
            | CallClosure _
            | Atom _ -> { last with Kind = Do(probe Return [] last.Place, last) }
            | _ ->
                let result = newLocal last.Type
                let given = { last with Kind = Atom(AVar result) }
                { last with Kind = Let(result, last, { last with Kind = Do(probe Return [] last.Place, given) }) }
        Chain.rebuild Chain.relink links last
    { body with Kind = Do(probe (Entry name) locals place, ending body) }
