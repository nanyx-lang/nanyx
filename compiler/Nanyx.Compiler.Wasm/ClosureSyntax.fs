/// The function values lambda lifting leaves in the syntax tree (see `LambdaLifting.lift`), as the core IR is given
/// them: a closure block is a call to the marker "__closure" with the lifted function's table slot, a call to the
/// marker "__slot" with its name, and then the values it captures. (A marker call rather than a tuple, so that
/// `f(closure)` is not flattened into one argument per field.) No code can write either name.
module internal Transpiler.Wasm.ClosureSyntax

open Parser.Program

let builtIn = "__closure"

/// The marker that names the lifted function whose table slot a closure block holds.
let slot = "__slot"

/// The name of the environment parameter every lifted function takes first: the closure block itself.
let envParameter = "__env"

/// The closure block for the lifted function `target` over `captured`, standing in for `origin` (the lambda or function
/// reference it replaces), whose id and span it keeps.
let make (origin: Expression) (target: string) (captured: Expression list) : Expression =
    { origin with
        Kind = FunctionCall(builtIn, None, Expression.synthetic (FunctionCall(slot, None, [ Expression.synthetic (IdentifierExpr target) ])) :: captured) }

/// A closure block's lifted function, and what it captures.
let (|Closure|_|) (kind: ExpressionKind) =
    match kind with
    | FunctionCall(name, _, Kind (FunctionCall(slotName, _, [ Kind (IdentifierExpr target) ])) :: captured) when name = builtIn && slotName = slot ->
        Some (target, captured)
    | _ -> None
