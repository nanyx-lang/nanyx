namespace Parser.Program

/// The names a test build's root gives the modules it imports (see `NyxCompiler.Package.testRootSource`): `__test0`,
/// `__test1` and so on, which no source can write. The backend names a module's exported def after the alias the root
/// imports it by, `__test0_perimeter`, and a crash's stack names it as the module's own code names it (#3081).
module TestRootSyntax =
    let private prefix = "__test"

    /// The alias of the root's import at `index`.
    let alias (index: int) = $"{prefix}{index}"

    /// What follows the alias in `name`, a name the backend gave a def the root imports, `perimeter` in
    /// `__test0_perimeter`.
    let (|Aliased|_|) (name: string) =
        if name.StartsWith prefix then
            let digits = name.Substring(prefix.Length) |> Seq.takeWhile System.Char.IsDigit |> Seq.length
            let rest = prefix.Length + digits
            if digits > 0 && rest < name.Length && name.[rest] = '_' then Some(name.Substring(rest + 1)) else None
        else None
