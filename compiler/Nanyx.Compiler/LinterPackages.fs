namespace NyxCompiler

open Parser.Program
open Parser.Program.DataValues

/// What a linter package declares it is, in its manifest's `package`: `linter = (root = "acme.lint", name = "acme",
/// rules = [(id = "noTodo", title = "TODO comment", severity = #info, description = "...")])`.
type LinterDeclaration =
    { /// The package that declares it.
      Package: string
      /// The module that exports `lint`, which a lint build of the package runs (see `LintBuilds`).
      Root: string
      /// The first part of its rules' codes: `acme` in `acme/noTodo`.
      Name: string
      /// Its rules, which a project's `settings` and `@allow` name, known without running it.
      Rules: Rule list }

/// Packages that are linters. A linter package says so in its own manifest, in its `package`'s `linter` field, with
/// its root module, its name and its rules. `Manifest.check` checks the field; this reads it. A project names the
/// linters it runs in its own manifest's `linters` (see `Manifest.LinterEntry`).
module LinterPackages =
    /// The field of a package's `package` that declares it a linter.
    let field = "linter"

    /// The fields of a linter's declaration.
    let rootField = "root"

    let nameField = "name"

    let rulesField = "rules"

    /// The fields of a rule of a linter's declaration.
    let ruleFields = [ "id"; "title"; "severity"; "unnecessary"; "description" ]

    /// The package every linter is written against, whose version must be the compiler's.
    let analysisPackage = "nanyx.analysis"

    /// The severities a rule's default can be, as a manifest writes them, without `#`.
    let defaultSeverities =
        [ "error", Severity.Error
          "warning", Severity.Warning
          "info", Severity.Info
          "hint", Severity.Hint ]

    let private text (fields: DataField list) (name: string) =
        match DataValue.field name fields with
        | Some { DataField.Value = { DataValue.Data = DataString text } } -> Some text
        | _ -> None

    /// The rule a rule's record in a declaration describes, if it is one (`Manifest.check` reports what is wrong).
    let private ruleOf (value: DataValue) : Rule option =
        match value.Data with
        | DataRecord fields ->
            let severity =
                match DataValue.field "severity" fields with
                | Some { DataField.Value = { DataValue.Data = DataTag(name, None) } } -> defaultSeverities |> List.tryFind (fst >> (=) name) |> Option.map snd
                | _ -> None
            let unnecessary =
                match DataValue.field "unnecessary" fields with
                | Some { DataField.Value = { DataValue.Data = DataBool unnecessary } } -> unnecessary
                | _ -> false
            match text fields "id", text fields "title", severity with
            | Some id, Some title, Some severity ->
                Some { Id = id; Title = title; DefaultSeverity = severity; Unnecessary = unnecessary; Description = defaultArg (text fields "description") "" }
            | _ -> None
        | _ -> None

    /// The linter the package `name`, whose manifest is `manifest`, declares it is, if it does.
    let ofManifest (name: string) (manifest: DataValue) : LinterDeclaration option =
        match manifest.Data with
        | DataRecord top ->
            match DataValue.field "package" top with
            | Some { DataField.Value = { DataValue.Data = DataRecord package } } ->
                match DataValue.field field package with
                | Some { DataField.Value = { DataValue.Data = DataRecord linter } } ->
                    let rules =
                        match DataValue.field rulesField linter with
                        | Some { DataField.Value = { DataValue.Data = DataList rules } } -> rules |> List.choose ruleOf
                        | _ -> []
                    Option.map2 (fun root linterName -> { Package = name; Root = root; Name = linterName; Rules = rules }) (text linter rootField) (text linter nameField)
                | _ -> None
            | _ -> None
        | _ -> None

    /// The error for naming the package `name` as a linter when its manifest doesn't declare it one.
    let notALinter (name: string) =
        $"'{name}' is not a linter: a linter's manifest declares that it is one, with the module that exports `lint`, its name and its rules, as `package = (..., {field} = ({rootField} = \"<module>\", {nameField} = \"<name>\", {rulesField} = [...]))`"
