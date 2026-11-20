/// The names the parser makes up, which no code can write: the parameters of lambdas the code doesn't name, and `???`.
[<AutoOpen>]
module Parser.Program.ImplicitNames

/// The parameters of a shorthand lambda: the operand its body leaves out (`{ * 2 }`, `{ .name }`), or the two
/// operands of an operator written alone (`{ + }`). Like `???`, they are names no code can write, so a shorthand
/// lambda never captures a name the code around it declares.
let implicitArgument = "<arg>"
let implicitSecondArgument = "<arg2>"

/// The parameter a lambda takes in place of the pattern written as its `position`th (counted from 1) parameter,
/// `{ Email(s) -> s }`, which a match at the top of its body takes apart. Like `implicitArgument`, no code can
/// write it.
let patternArgument (position: int) = $"<pattern{position}>"

/// `???`, the placeholder for code not written yet. It is read as a name no other code can write, so every pass that
/// handles names handles it; the type checker reports it as not implemented.
let notImplemented = "???"
