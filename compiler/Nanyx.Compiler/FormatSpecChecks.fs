namespace NyxCompiler

open Parser.Program

/// The format specs written in holes, `"{price:.2f}"`, checked once the module is typed against the type of the value
/// each writes: a spec that value's `tryFormat` would reject is an error as the program compiles (NYX0307), at the
/// spec, rather than a crash where it runs. Only a value whose `tryFormat` is the prelude's is checked, as
/// `FormatSpecs` reads specs as those do; a type of the program's own, or a type variable, is checked as it runs.
module internal FormatSpecChecks =
    /// What a value of the type `ty` is as `FormatSpecs` checks a spec for it, if its `tryFormat` is the prelude's or
    /// the one the compiler derives for a unit quantity (see `StructuralInstances`), whose units the module sees
    /// `unitsOf` gives.
    let private formatted (unitsOf: Dimension -> string list) (ty: Ty) : FormatSpecs.Formatted option =
        let measured (dimension: Dimension) =
            // In the order `Dimension.text` writes the parts in, as the derived `tryFormat` has them.
            let dimension = Dimension.multiply dimension []
            let partUnits =
                dimension |> List.indexed |> List.collect (fun (part, (id, _)) -> unitsOf (Dimension.ofBase id) |> List.distinct |> List.map (fun name -> name, part))
            FormatSpecs.Formatted.Measured(Dimension.text dimension, unitsOf dimension, partUnits, dimension |> List.map snd, dimension |> List.map (fst >> _.Name))
        match Unifier.unalias ty with
        | TyMeasure(dimension, storage) ->
            match Unifier.unalias storage with
            | TyPrimitive("int" | "float" | "F32") -> Some (measured dimension)
            | SizedIntegerType integer when SizedInteger.bits integer < 64 ->
                Some (measured dimension)
            | _ -> None
        | TyPrimitive "int"
        | SizedIntegerType _ -> Some FormatSpecs.Formatted.Integer
        | TyPrimitive("float" | "F32") -> Some FormatSpecs.Formatted.Float
        | TyPrimitive("string" | "char" | "bool") -> Some FormatSpecs.Formatted.Text
        | TyNominal({ Home = StandardLibraryModule home; Name = "Instant" }, _, _, _) when home = ModuleOrigin.preludeName ->
            Some FormatSpecs.Formatted.Instant
        | _ -> None

    /// Where the spec written at `span` stops being one, at its byte `at`: the character there, or the whole spec when
    /// the spec ends before it.
    let private spanAt (span: SourceSpan) (spec: string) (at: int) =
        let offset = FormatSpecs.charAt spec at
        if offset >= spec.Length then span
        else
            let width = if System.Char.IsHighSurrogate spec[offset] && offset + 1 < spec.Length then 2 else 1
            { span with Start = span.Start + offset; End = span.Start + offset + width; StartColumn = span.StartColumn + offset }

    /// An error for each spec written in a hole of `items` that the type of the value it writes rejects, reading each
    /// value's type from `typeOf`, resolved, and the names of a dimension's units the module sees, the largest first,
    /// from `unitsOf`. A value whose type failed to check is left to that error.
    let errors (typeDefs: Map<string, TypeDefInfo>) (unitsOf: Dimension -> string list) (typeOf: NodeId -> Ty option) (items: TopLevelItem list) : Diagnostic list =
        items
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.choose (fun expr ->
            match expr.Kind with
            | FormatSyntax.Call(value, spec, specSpan) ->
                typeOf value.Id
                |> Option.filter (Unifier.holdsError >> not)
                |> Option.bind (fun ty -> formatted unitsOf ty |> Option.map (fun kind -> ty, kind))
                |> Option.bind (fun (ty, kind) ->
                    FormatSpecs.check kind spec
                    |> Option.map (fun bad ->
                        Diagnostics.at DiagnosticCodes.BadFormatSpec (spanAt specSpan spec bad.At)
                            $"'{spec}' is no format spec for a '{Unifier.typeText typeDefs ty}': at {bad.At}, it could have {bad.Expected}"))
            | _ -> None)
