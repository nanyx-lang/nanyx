namespace NyxCompiler

/// Composition: `f >> g` is the function that applies `f` and then `g` to what `f` returns, and `g << f` is the same
/// function. Each is a call to a function of the prelude's, as `**` is (see `ArithmeticOperators.inferPower`). Their
/// names are reserved, so that no definition a module makes changes what the operators mean.
module internal CompositionOperators =
    /// The prelude function that the composition operator `op` calls, when `op` is one.
    let functionFor (op: string) =
        match op with
        | ">>" -> Some "__composeForward"
        | "<<" -> Some "__composeBackward"
        | _ -> None
