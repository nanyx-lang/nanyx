/// A string literal is a `char` where a `char` is expected when it is checked, as `def initial: char = "A"` is, and a
/// `string` anywhere else (#2596). What expects one: the type a def's value, an argument or an item of a literal is
/// checked against (see `ExpectedLiterals`), the value a literal pattern matches, and the other operand of `==` or of an
/// ordering operator. Where it is a `char`, it must be exactly one Unicode scalar value (`NYX0284`). A literal whose
/// `char`-ness only comes later, as in `{ c -> c == "a" }` with nothing known of `c`, is a `string`.
module internal NyxCompiler.CharLiterals

open System.Globalization
open System.Text
open Parser.Program
open NyxCompiler.InferState

/// Whether the type `ty` is, as far as `state` knows it, `char`.
let isChar (state: InferState) (ty: Ty) : bool =
    match resolveSoFar state ty |> Unifier.unalias with
    | Chars.CharType -> true
    | _ -> false

/// Whether `rune` is a combining mark, which follows the character it marks.
let private isCombining (rune: Rune) =
    match Rune.GetUnicodeCategory rune with
    | UnicodeCategory.NonSpacingMark
    | UnicodeCategory.SpacingCombiningMark
    | UnicodeCategory.EnclosingMark -> true
    | _ -> false

/// The error for a string literal at `span` where a `char` is expected that is not one Unicode scalar value: its text,
/// or none for one with holes.
let private notOne (span: SourceSpan) (text: string option) : Diagnostic =
    let why =
        match text with
        | None -> "a literal with holes is a string"
        | Some "" -> "this one is empty"
        | Some text ->
            let runes = text.EnumerateRunes() |> List.ofSeq
            if runes.Tail |> List.exists isCombining then
                $"'{text}' is {runes.Length} scalar values, '{runes.Head}' and a combining mark: write the character as one scalar value, if Unicode has one, or use a string"
            else
                $"'{text}' is {runes.Length} characters"
    Diagnostics.at
        DiagnosticCodes.NotOneChar
        span
        $"A char is one Unicode scalar value, so a string literal where a 'char' is expected must be exactly one character: {why}"

/// The type of the string literal `text`, at `span`, where a `char` is expected: `char`, if it is one scalar value.
let typeOf (span: SourceSpan) (text: string) : Result<Ty, Diagnostic list> =
    match Chars.scalarOf text with
    | Some _ -> Ok Chars.ty
    | None -> Error [ notOne span (Some text) ]

/// The error for a string with holes, at `span`, where a `char` is expected.
let withHoles (span: SourceSpan) : Result<'a, Diagnostic list> = Error [ notOne span None ]

/// `message`, a type mismatch's of the kind `kind`, with how to make a literal a `char` when the mismatch is of a `char`
/// and a `string`.
let withHint (kind: MismatchKind) (message: string) : string =
    match kind with
    | CharAndString ->
        message + $": a string literal is a char only where a char is expected, so write the type, as in `{{ c: {Chars.name} -> ... }}`"
    | _ -> message
