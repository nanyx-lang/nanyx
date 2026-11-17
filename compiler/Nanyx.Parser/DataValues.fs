/// A file that is one value written as data, with no code in it: a package's `.nyxp` manifest.
module Parser.Program.DataValues

open System
open System.Text.RegularExpressions

/// A value written as data, and where it is written.
type DataValue =
    { Data: DataKind
      Location: SourceSpan }

and DataKind =
    | DataRecord of DataField list
    | DataList of DataValue list
    | DataTuple of DataValue list
    | DataString of string
    | DataInt of bigint
    | DataFloat of float
    | DataBool of bool
    /// `#error`, or `#name(value)`.
    | DataTag of string * DataValue option

/// A record's field: `name = value`.
and DataField =
    { Name: string
      /// Where the field's name is written.
      NameSpan: SourceSpan
      Value: DataValue }

/// Why a file is not one data value.
type DataError =
    /// The file is not Nanyx.
    | NotParsed of ParseError
    /// Something in it is code, not data: a name, a call, an operator, a definition. The message says what.
    | NotData of SourceSpan * string

module DataValue =
    /// What a value is, as a message names it: "a record", "the string \"red\"".
    let describe (value: DataValue) =
        match value.Data with
        | DataRecord _ -> "a record"
        | DataList _ -> "a list"
        | DataTuple _ -> "a tuple"
        | DataString text -> $"the string \"{text}\""
        | DataInt n -> string n
        | DataFloat n -> string n
        | DataBool b -> if b then "true" else "false"
        | DataTag(name, _) -> $"#{name}"

    /// The field called `name` of a record's `fields`, if it has one.
    let field (name: string) (fields: DataField list) = fields |> List.tryFind (fun field -> field.Name = name)

    /// A directive's word read as a value, as a manifest writes one: a tag, `#error`, a whole number, `4`, a string in
    /// quotes, `"text"`, or `true` or `false`. None for a word that is none of them.
    let ofWord (word: DirectiveWord) : DataValue option =
        let value kind = Some { Data = kind; Location = word.Span }
        let text = word.Text
        if Regex.IsMatch(text, @"^#[A-Za-z_][A-Za-z0-9_]*$") then value (DataTag(text.Substring 1, None))
        elif Regex.IsMatch(text, @"^-?[0-9]+$") then value (DataInt(bigint.Parse text))
        elif text.Length >= 2 && text.StartsWith "\"" && text.EndsWith "\"" then
            value (DataString(text.Substring(1, text.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\")))
        elif text = "true" || text = "false" then value (DataBool(text = "true"))
        else None

    /// The span from `start` to `finish` of `source`, a file's normalised text.
    let private spanIn (file: string) (source: string) (start: int) (finish: int) =
        let lineStart = if start = 0 then 0 else source.LastIndexOf('\n', start - 1) + 1
        { File = file
          Start = start
          End = finish
          StartLine = source.Substring(0, start) |> Seq.filter ((=) '\n') |> Seq.length
          StartColumn = start - lineStart }

    /// What `expression` is as code, for the message that says it is not data.
    let private whatCode (expression: Expression) =
        match expression.Kind with
        | IdentifierExpr name -> $"The name '{name}'"
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) -> $"A call of '{name}'"
        | BinaryOp(op, _, _, _) -> $"The operator `{op}`"
        | Lambda _ -> "A lambda"
        | InterpolatedString _ -> "An interpolated string"
        | UnitExpr -> "`()`"
        | _ -> "Code"

    /// `expression` as data, or each place in it that is code. `source` is the normalised text of the file it is
    /// written in, where a field's name is found.
    let rec private ofExpression (source: string) (expression: Expression) : Result<DataValue, DataError list> =
        let value kind = Result.Ok { Data = kind; Location = expression.Span }
        let all (expressions: Expression list) =
            let results = expressions |> List.map (ofExpression source)
            match results |> List.collect (function Result.Error errors -> errors | Result.Ok _ -> []) with
            | [] -> Result.Ok(results |> List.choose (function Result.Ok value -> Some value | Result.Error _ -> None))
            | errors -> Result.Error errors
        match expression.Kind with
        | LiteralExpr(StringLit text) -> value (DataString text)
        | LiteralExpr(IntLit n) -> value (DataInt n)
        | LiteralExpr(FloatLit n) -> value (DataFloat n)
        | LiteralExpr(BoolLit b) -> value (DataBool b)
        | TagExpr(name, None) -> value (DataTag(name, None))
        | TagExpr(name, Some payload) -> ofExpression source payload |> Result.bind (fun payload -> value (DataTag(name, Some payload)))
        | ListExpr items -> all items |> Result.bind (DataList >> value)
        | TupleExpr items -> all items |> Result.bind (DataTuple >> value)
        | RecordExpr fields when fields |> List.forall (function PositionalField _ -> true | NamedField _ -> false) ->
            all (fields |> List.map (function PositionalField item | NamedField(_, item) -> item)) |> Result.bind (DataTuple >> value)
        | RecordExpr fields ->
            let named =
                fields
                |> List.map (function
                    | NamedField(name, item) -> Result.Ok(name, item)
                    | PositionalField item ->
                        Result.Error [ NotData(item.Span, "a record's fields all have names here: name = value") ])
            match named |> List.collect (function Result.Error errors -> errors | Result.Ok _ -> []) with
            | [] ->
                let named = named |> List.choose (function Result.Ok field -> Some field | Result.Error _ -> None)
                all (List.map snd named)
                |> Result.bind (fun values ->
                    List.map2
                        (fun (name, item: Expression) value ->
                            // The name is the last one written before the value, `name =`.
                            let pattern = Regex($@"\b{Regex.Escape name}\s*=\s*$")
                            let before = source.Substring(0, item.Span.Start)
                            let found = pattern.Match before
                            let nameSpan =
                                if found.Success then spanIn item.Span.File source found.Index (found.Index + name.Length)
                                else item.Span
                            { Name = name; NameSpan = nameSpan; Value = value })
                        named
                        values
                    |> DataRecord
                    |> value)
            | errors -> Result.Error errors
        | _ -> Result.Error [ NotData(expression.Span, $"{whatCode expression} is code, but this file holds only data") ]

    /// The text of the file at `filePath` read as one data value: record, list and tuple literals, strings, numbers,
    /// `true` and `false`, and tags. A name, a call, an operator, a definition or anything else that is code is an
    /// error, so the file is read without running or type-checking anything.
    let read (filePath: string) (text: string) : Result<DataValue, DataError list> =
        let source = normalizeSource text
        let directives =
            Directives.read filePath source
            |> List.map (fun directive -> NotData(directive.Span, "A `::` directive is for a script, but this file holds only data"))
        match runModuleParser filePath source with
        | Result.Error error -> Result.Error [ NotParsed error ]
        | Result.Ok (items, _) ->
            let notData (item: TopLevelItem) =
                NotData(item.Span, "this file holds one value written as data, and no definitions, imports or attributes")
            match directives, items with
            | [], [ Kind (Expr expression) ] -> ofExpression source expression
            | _, [] when directives.IsEmpty ->
                Result.Error [ NotData(spanIn filePath source 0 0, "this file is empty, but it should hold one value written as data") ]
            | _ ->
                let errors =
                    items
                    |> List.collect (fun item ->
                        match item.Kind with
                        | Expr expression when items.Length = 1 ->
                            match ofExpression source expression with
                            | Result.Error errors -> errors
                            | Result.Ok _ -> []
                        | _ -> [ notData item ])
                Result.Error(directives @ errors)
