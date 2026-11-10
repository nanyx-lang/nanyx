/// Checking that a block has a value to give back.
module NyxCompiler.BlockResult

open Parser.Program
open NyxCompiler

/// What kind of statement `kind` is, as a message names it.
let private describe (kind: StatementKind) =
    match kind with
    | DefStatement(_, name, _, _) -> $"the definition of '{name}'"
    | TypeDefStatement(name, _, _, _) -> $"the type '{name}'"
    | ImportStatement _ -> "an import"
    | UseStatement _ -> "a `use`"
    | ExprStatement _ -> "an expression"

/// A block's value is that of its last statement, so a block that does not end with an expression has none. An empty
/// block is unit; any other is an error, as in `{ i -> def r = f(i) }`, where the `r` the block should give back has
/// been left off.
let internal check (statements: Statement list) : Diagnostic list =
    match List.tryLast statements with
    | Some { Kind = ExprStatement _ }
    | None -> []
    | Some last ->
        let message =
            $"This block ends with {describe last.Kind}, so it has no value to give back. End it with the expression whose value it gives back, or with () if it gives back nothing."
        [ Diagnostics.at DiagnosticCodes.BlockWithoutResult last.Span message ]
