/// What hovering a built-in type's name in a type shows (see `Hover.builtInTypeAt`): what the type is, from one table,
/// and the other name it is written by, where it has one, as `int` is `I64`. A capitalised name, `List` or `I64`, is
/// looked up by its alias (see `BuiltinTypeNames.aliasOf`), so both spellings show the same.
module internal NyxCompiler.BuiltInTypeHover

/// The docs page on the sized numbers, and the one on the other built-in types.
let private sizedNumbers = "[Sized numbers](https://nanyx.dev/docs/advanced/sized-numbers)"
let private types = "[Types](https://nanyx.dev/docs/tour/types)"

/// A sized integer's description: how many bits it has and whether it is signed.
let private sizedInteger (integer: SizedInteger) =
    let name = SizedInteger.name integer
    let sign = if SizedInteger.isSigned integer then "signed" else "unsigned"
    name, $"type {name}", $"A {SizedInteger.bits integer}-bit {sign} integer. See {sizedNumbers}."

/// Each built-in type, by the name the compiler works with: what hover writes for it, and what it says after.
let private descriptions : Map<string, string * string> =
    [ "int", "type int = I64", $"`int` is another name for `I64`, a 64-bit signed integer on every target: an `int` is an `I64`, not converted to one. See {sizedNumbers}."
      "float", "type float = F64", $"`float` is another name for `F64`, a 64-bit floating-point number: a `float` is an `F64`. See {sizedNumbers}."
      F32.name, $"type {F32.name}", $"A 32-bit floating-point number. See {sizedNumbers}."
      "bool", "type bool", $"`true` or `false`, also written `Boolean`. See {types}."
      "string", "type string", $"Text, as UTF-8, also written `String`. See {types}."
      Chars.name, $"type {Chars.name}", $"One Unicode scalar value, written `'a'`. See {types}."
      "unit", "type ()", $"The type of `()`, the value that says nothing. See {types}."
      Lists.typeName, $"type {Lists.typeName}(a)", $"The built-in list type, also written `List`: `List(int)` is `list(int)`, the same type. See {types}."
      Maps.typeName, $"type {Maps.typeName}(k, v)", $"The built-in map type, also written `Map`. See {types}."
      Sets.typeName, $"type {Sets.typeName}(a)", $"The built-in set type, also written `Set`. See {types}."
      Arrays.typeName, $"type {Arrays.typeName}(a)", $"The built-in array type, also written `Array`. See {types}." ]
    @ (SizedInteger.all |> List.map sizedInteger)
    |> List.map (fun (name, written, note) -> name, (written, note))
    |> Map.ofList

/// What hover writes for the built-in type `written` names, as `int` or `I64`, and what it says after, if it is one.
let describe (written: string) : (string * string) option =
    descriptions |> Map.tryFind (BuiltinTypeNames.aliasOf written |> Option.defaultValue written)
