/// The keys the backend knows context members by.
namespace Transpiler.Wasm

/// A type as an instance of a parameterised context is written with it: its name and its type arguments
/// (`ToString(int)`, `list(a)`). A structural type is keyed by a name no type can have, with its parts: `(,)` for a
/// tuple, `#x` for a tag, `|` for a union (see `Identifiers.typeKeyOf`).
type TypeKey = TypeKey of name: string * arguments: TypeKey list

/// The key a context member is known by in the backend, in scope and among the members a function takes.
type ContextMemberKey =
    /// A member known by its name alone: one of a context without parameters (`println`), one a `use` of a record
    /// binds, or another name a member is in scope by.
    | NamedMember of string
    /// The member of an instance of a parameterised context (`toString` of `ToString(int)`).
    | InstanceMember of instance: TypeKey * memberName: string

module ContextMemberKey =
    /// A name in a key's text, with each `_` written `-5F-`, so that `_` only ever separates the parts.
    let private part (name: string) = name.Replace("_", "-5F-")

    let rec private typeText (TypeKey(name, arguments)) =
        part name :: (arguments |> List.map typeText) |> String.concat "_"

    /// `key` as text, for the names of the parameters and locals that hold its member: the member's name, or
    /// `<context>_<type arguments>_<member>` for an instance's member (`ToString_int_toString`). Two keys never have the
    /// same text, since a `_` in a name is written `-5F-` (`To_Text_a_to_text` is `To-5F-Text_a_to-5F-text`).
    let text (key: ContextMemberKey) =
        match key with
        | NamedMember name -> part name
        | InstanceMember(instance, memberName) -> $"{typeText instance}_{part memberName}"

    /// Whether `available` is the member `required` is, at other type arguments maybe: the same member of the same
    /// context (a required `toString` of `ToString(a)` and an available one of `ToString(int)`).
    let sameMember (required: ContextMemberKey) (available: ContextMemberKey) =
        match required, available with
        | InstanceMember(TypeKey(context, _), memberName), InstanceMember(TypeKey(context', _), memberName') ->
            context = context' && memberName = memberName'
        | _ -> false
