namespace NyxCompiler

open System
open Parser.Program

/// Which values patterns match, as far as the compiler reasons about it: what the exhaustiveness check
/// (`Infer`) and the `unreachableArms` rule (`Linters`) both ask. Both are questions of usefulness, as in Maranget's
/// "Warnings for pattern matching": whether a row of patterns matches a value that no row before it does. A pattern
/// it cannot reason about (a guard, a list with a splat in the middle) is taken to match no value for sure when it is
/// in one of the rows before, and any value when it is the row asked about, so neither ever reports something on its
/// account that might not be so.
module PatternSpace =
    /// The patterns of an arm of a match on `valueCount` values. A lone `else` is the arm for anything else, whatever
    /// every value is, so in a match on several values it stands for a catch-all at each position: one `else` per
    /// value, each written where the `else` is. Brackets around a tuple are optional, as in `def x, y = pair`, so
    /// several values and a tuple of them are matched alike: in a match on one value, `| x, y` is `| (x, y)`, and in a
    /// match on two, `| (x, y)` is `| x, y`. Any other arm is as written.
    let ofArm (valueCount: int) (patterns: Pattern list) : Pattern list =
        match patterns with
        | [ Kind ElsePattern as orElse ] when valueCount > 1 ->
            orElse :: List.init (valueCount - 1) (fun _ -> Pattern.at orElse.Span ElsePattern)
        | first :: _ :: _ when valueCount = 1 ->
            [ Pattern.at (SourceSpan.between first.Span (List.last patterns).Span) (TuplePattern patterns) ]
        | [ Kind (TuplePattern elements) ] when valueCount > 1 && elements.Length = valueCount -> elements
        | _ -> patterns

    /// Whether `pattern` matches every value of its type. `(a, _)` matches every tuple: a tuple pattern is irrefutable
    /// when each element is. So is a record pattern (`$Money(pence)`, `(x = a)`) whose parts all are: the type says the
    /// value has that shape.
    let rec isCatchAll (pattern: Pattern) =
        match pattern.Kind with
        | WildcardPattern
        | UnitPattern
        | ElsePattern
        | IdentifierPattern _ -> true
        | TuplePattern elements
        | RecordPattern(_, elements) -> elements |> List.forall isCatchAll
        | RecordMemberPattern(members, _) -> members |> List.forall (snd >> isCatchAll)
        | AndPattern parts -> parts |> List.forall isCatchAll
        | LiteralPattern _
        | ListPattern _
        | ListSplatMiddle _
        | RangePattern _
        | GuardPattern _
        | TagPattern _ -> false

    /// The tag an arm whose patterns are `patterns` handles in full at `index`: its pattern there is that tag with a
    /// catch-all payload, or none, and its pattern at every other position is a catch-all. Every value with that tag
    /// there goes to the arm.
    let tagHandled (patterns: Pattern list) (index: int) : string option =
        let othersCatchAll = patterns |> List.indexed |> List.forall (fun (i, pattern) -> i = index || isCatchAll pattern)
        match List.tryItem index patterns with
        | Some (Kind (TagPattern(tagName, payload))) when othersCatchAll && payload |> Option.forall isCatchAll -> Some tagName
        | _ -> None

    let rec private unwrap (ty: Ty) =
        match ty with
        | TyNominal(_, underlying, _)
        | TyAlias(_, _, underlying) -> unwrap underlying
        | _ -> ty

    let rec private unalias (ty: Ty) =
        match ty with
        | TyAlias(_, _, underlying) -> unalias underlying
        | _ -> ty

    /// The tags of `ty`, if it is a union of tags and nothing else (no rest left open), or a single tag.
    let private tagsOf (ty: Ty) : Set<string> option =
        match unwrap ty with
        | TyUnion items when items |> List.forall (function TyTag _ -> true | _ -> false) ->
            items |> List.choose (function TyTag(name, _) -> Some name | _ -> None) |> Set.ofList |> Some
        | TyTag(name, _) -> Some (Set.singleton name)
        | _ -> None

    /// A value of a type, as an example of one no arm matches. `Anything` is any value at all; `OneOfTags` any value
    /// with one of those tags, which are every tag of the type that no arm names.
    type Witness =
        | Anything
        | OneOfTags of string list
        | TagCase of string * Witness
        | BoolCase of bool
        | IntCase of int64
        | LiteralCase of Literal
        | RecordCase of (string * Witness) list
        | NamedCase of string * Witness list
        | EmptyList
        | ListCons of Witness * Witness

    /// What a pattern matches, as the check reasons about it. A tuple is a record whose fields are `0`, `1` and so on,
    /// as its type is, and a list is empty or an element before another list.
    type private Space =
        | Any
        /// No value that can be counted on.
        | Never
        | Tag of string * Space
        | Bool of bool
        /// The ints from the first to the second, inclusive.
        | Ints of int64 * int64
        /// A string or a float.
        | Lit of Literal
        | Fields of Map<string, Space>
        | Named of string * Space list
        | Nil
        | Cons of Space * Space

    /// One kind of value of a type, which the check takes a column of patterns apart by: those of a tag, a boolean, a
    /// run of ints that no pattern of the column splits, a string or float, a record with these fields, a value of a
    /// named record type, an empty list, a list with an element.
    type private Head =
        | HTag of string
        | HBool of bool
        | HInts of int64 * int64
        | HLit of Literal
        | HFields of string list
        | HNamed of string * int
        | HNil
        | HCons

    let private intOf (expr: Expression) =
        match expr.Kind with
        | LiteralExpr (IntLit value) -> Some (int64 value)
        | _ -> None

    /// What `pattern` matches. `certain` says what a pattern the check cannot reason about stands for: nothing when
    /// it is in an arm that might cover others (`true`), anything when it is in the arm asked about.
    let rec private spaceOf (certain: bool) (pattern: Pattern) : Space =
        let uncertain = if certain then Never else Any
        match pattern.Kind with
        | WildcardPattern
        | UnitPattern
        | ElsePattern
        | IdentifierPattern _ -> Any
        | LiteralPattern (IntLit value) -> Ints(int64 value, int64 value)
        | LiteralPattern (BoolLit value) -> Bool value
        | LiteralPattern literal -> Lit literal
        | RangePattern(low, high) ->
            match intOf low, intOf high with
            | Some low, Some high when low <= high -> Ints(low, high)
            | _ -> uncertain
        | TagPattern(name, payload) -> Tag(name, payload |> Option.map (spaceOf certain) |> Option.defaultValue Any)
        | TuplePattern elements -> elements |> List.mapi (fun index element -> string index, spaceOf certain element) |> Map.ofList |> Fields
        | RecordMemberPattern(members, _) -> members |> List.map (fun (name, member') -> name, spaceOf certain member') |> Map.ofList |> Fields
        | RecordPattern(name, elements) -> Named(name, elements |> List.map (spaceOf certain))
        | ListPattern(elements, rest) ->
            let tail = rest |> Option.map (spaceOf certain) |> Option.defaultValue Nil
            List.foldBack (fun element after -> Cons(spaceOf certain element, after)) elements tail
        | AndPattern parts -> parts |> List.map (spaceOf certain) |> List.reduce (intersect uncertain)
        | ListSplatMiddle _
        | GuardPattern _ -> uncertain

    /// What both `left` and `right` match. `uncertain` stands for what the check cannot tell, as in `spaceOf`: two
    /// spaces with no value in common are that, so an arm asked about is never said to match nothing on their account.
    and private intersect (uncertain: Space) (left: Space) (right: Space) : Space =
        let recur = intersect uncertain
        match left, right with
        | Any, other
        | other, Any -> other
        | Never, _
        | _, Never -> Never
        | Tag(name, payload), Tag(other, otherPayload) when name = other -> Tag(name, recur payload otherPayload)
        | Bool value, Bool other when value = other -> left
        | Ints(low, high), Ints(otherLow, otherHigh) when max low otherLow <= min high otherHigh ->
            Ints(max low otherLow, min high otherHigh)
        | Lit literal, Lit other when literal = other -> left
        | Fields fields, Fields others ->
            Map.fold (fun merged name space -> merged |> Map.add name (merged |> Map.tryFind name |> Option.map (recur space) |> Option.defaultValue space)) fields others
            |> Fields
        | Named(name, parts), Named(other, otherParts) when name = other && parts.Length = otherParts.Length ->
            Named(name, List.map2 recur parts otherParts)
        | Nil, Nil -> Nil
        | Cons(head, tail), Cons(otherHead, otherTail) -> Cons(recur head otherHead, recur tail otherTail)
        | _ -> uncertain

    let private arity (head: Head) =
        match head with
        | HTag _ -> 1
        | HFields names -> names.Length
        | HNamed(_, count) -> count
        | HCons -> 2
        | HBool _
        | HInts _
        | HLit _
        | HNil -> 0

    /// The kind of value `space` matches, for one that is neither `Any`, `Never`, ints nor fields.
    let private headOf (space: Space) =
        match space with
        | Tag(name, _) -> Some (HTag name)
        | Bool value -> Some (HBool value)
        | Lit literal -> Some (HLit literal)
        | Named(name, parts) -> Some (HNamed(name, parts.Length))
        | Nil -> Some HNil
        | Cons _ -> Some HCons
        | Any
        | Never
        | Ints _
        | Fields _ -> None

    /// What `space` matches inside a value of the kind `head`, part by part, if it matches every such value's shape.
    let private partsOf (head: Head) (space: Space) : Space list option =
        match head, space with
        | _, Any -> Some (List.replicate (arity head) Any)
        | HTag name, Tag(other, payload) when name = other -> Some [ payload ]
        | HBool value, Bool other when value = other -> Some []
        | HInts(low, high), Ints(otherLow, otherHigh) when otherLow <= low && high <= otherHigh -> Some []
        | HLit literal, Lit other when literal = other -> Some []
        | HFields names, Fields fields -> Some (names |> List.map (fun name -> fields |> Map.tryFind name |> Option.defaultValue Any))
        | HNamed(name, count), Named(other, parts) when name = other && parts.Length = count -> Some parts
        | HNil, Nil -> Some []
        | HCons, Cons(head, tail) -> Some [ head; tail ]
        | _ -> None

    /// The types of the parts of a value of the kind `head` whose type is `ty`, where known.
    let private partTypes (ty: Ty option) (head: Head) : Ty option list =
        let shape = ty |> Option.map unwrap
        match head with
        | HTag name ->
            let payload =
                match shape with
                | Some (TyUnion items) -> items |> List.tryPick (function TyTag(other, payload) when other = name -> Some payload | _ -> None) |> Option.flatten
                | Some (TyTag(other, payload)) when other = name -> payload
                | _ -> None
            [ payload ]
        | HFields names ->
            let fields =
                match shape with
                | Some (TyRecord fields)
                | Some (TyOpenRecord(fields, _)) -> fields
                | _ -> Map.empty
            names |> List.map fields.TryFind
        | HNamed(_, count) ->
            match ty |> Option.map unalias with
            | Some (TyNominal(_, underlying, _)) when count = 1 -> [ Some underlying ]
            | _ -> List.replicate count None
        | HCons ->
            let element =
                match shape with
                | Some (TyApply("list", [ element ])) -> Some element
                | _ -> None
            [ element; ty ]
        | HBool _
        | HInts _
        | HLit _
        | HNil -> []

    /// The value of the kind `head` with these parts.
    let private witnessOf (head: Head) (parts: Witness list) =
        match head, parts with
        | HTag name, [ payload ] -> TagCase(name, payload)
        | HBool value, _ -> BoolCase value
        | HInts(low, high), _ -> IntCase (if low > 0L then low elif high < 0L then high else 0L)
        | HLit literal, _ -> LiteralCase literal
        | HFields names, _ -> RecordCase (List.zip names parts)
        | HNamed(name, _), _ -> NamedCase(name, parts)
        | HNil, _ -> EmptyList
        | HCons, [ head; tail ] -> ListCons(head, tail)
        | _ -> Anything

    /// The runs of ints from `low` to `high` that no range in `ranges` splits: each is in a range in full, or in none.
    let private runs (low: int64) (high: int64) (ranges: (int64 * int64) list) =
        let cuts =
            ranges
            |> List.collect (fun (start, finish) -> [ start; (if finish = Int64.MaxValue then finish else finish + 1L) ])
            |> List.filter (fun cut -> low < cut && cut <= high)
            |> List.distinct
            |> List.sort
        List.zip (low :: cuts) ((cuts |> List.map (fun cut -> cut - 1L)) @ [ high ])

    /// Fields in the order a tuple's are written, then by name.
    let private fieldOrder (name: string) =
        match Int32.TryParse name with
        | true, index -> 0, index, ""
        | _ -> 1, 0, name

    /// A string or float none of `present` is, of the kind `sample` is.
    let private otherLiteral (present: Literal list) (sample: Literal) =
        let candidates =
            match sample with
            | StringLit _ -> Seq.append [ "" ] (Seq.initInfinite (fun index -> string (char (int 'a' + index % 26)) + (if index < 26 then "" else string (index / 26)))) |> Seq.map StringLit
            | _ -> Seq.initInfinite (float >> FloatLit)
        candidates |> Seq.find (fun candidate -> not (List.contains candidate present))

    /// A value matched by `vector` (one space per value, of the types `types`) that no row of `rows` matches, if there
    /// is one: one witness per value. `trustTags` says whether the tags the rows name are taken to be every tag of a
    /// type that is not known to be a closed union of tags.
    let rec private useful (trustTags: bool) (rows: Space list list) (vector: Space list) (types: Ty option list) : Witness list option =
        match vector with
        | [] -> if rows.IsEmpty then Some [] else None
        | first :: rest ->
            let ty = List.tryHead types |> Option.flatten
            let restTypes = List.tail (if types.IsEmpty then [ None ] else types)
            let column = rows |> List.map List.head
            // The rows that match a value of the kind `head`, its parts in front of their other patterns.
            let through (head: Head) (parts: Space list) =
                let specialised = rows |> List.choose (fun row -> partsOf head row.Head |> Option.map (fun inner -> inner @ row.Tail))
                useful trustTags specialised (parts @ rest) (partTypes ty head @ restTypes)
                |> Option.map (fun witnesses -> witnessOf head (List.take parts.Length witnesses) :: List.skip parts.Length witnesses)
            let anyOf (heads: Head list) = heads |> List.tryPick (fun head -> through head (List.replicate (arity head) Any))
            // A kind of value no row names: the rows that match it are those that match anything.
            let otherwise (missing: Witness) =
                let defaulted = rows |> List.choose (fun row -> match row.Head with Any -> Some row.Tail | _ -> None)
                useful trustTags defaulted rest restTypes |> Option.map (fun witnesses -> missing :: witnesses)
            let ranges = column |> List.choose (function Ints(low, high) -> Some (low, high) | _ -> None)
            let fieldNames =
                column
                |> List.collect (function Fields fields -> fields |> Map.toList |> List.map fst | _ -> [])
            let fieldsOf (own: string list) = own @ fieldNames |> List.distinct |> List.sortBy fieldOrder
            match first with
            | Never -> None
            | Ints(low, high) -> runs low high ranges |> List.tryPick (fun run -> through (HInts run) [])
            | Fields own ->
                let names = fieldsOf (own |> Map.toList |> List.map fst)
                through (HFields names) (names |> List.map (fun name -> own |> Map.tryFind name |> Option.defaultValue Any))
            | Any ->
                let heads = column |> List.choose headOf |> List.distinct
                let hasFields = column |> List.exists (function Fields _ -> true | _ -> false)
                let kinds =
                    [ if not ranges.IsEmpty then "int"
                      if hasFields then "fields"
                      yield!
                          heads
                          |> List.map (function
                              | HTag _ -> "tag"
                              | HBool _ -> "bool"
                              | HLit (StringLit _) -> "string"
                              | HLit _ -> "float"
                              | HNamed _ -> "named"
                              | HNil
                              | HCons -> "list"
                              | HInts _
                              | HFields _ -> "") ]
                    |> List.distinct
                match kinds with
                | [] -> otherwise Anything
                | [ "tag" ] ->
                    let present = heads |> List.choose (function HTag name -> Some name | _ -> None) |> Set.ofList
                    match ty |> Option.bind tagsOf with
                    | Some union when Set.isSubset union present -> anyOf (present |> Set.toList |> List.map HTag)
                    | Some union -> otherwise (OneOfTags (Set.difference union present |> Set.toList))
                    | None when trustTags -> anyOf (present |> Set.toList |> List.map HTag)
                    | None -> otherwise Anything
                | [ "bool" ] ->
                    match heads with
                    | [ HBool value ] -> otherwise (BoolCase (not value))
                    | _ -> anyOf [ HBool true; HBool false ]
                | [ "int" ] ->
                    let all = runs Int64.MinValue Int64.MaxValue ranges
                    let covered (low, high) = ranges |> List.exists (fun (start, finish) -> start <= low && high <= finish)
                    match all |> List.filter (covered >> not) with
                    | [] -> anyOf (all |> List.map HInts)
                    | gaps -> otherwise (gaps |> List.map (fun gap -> witnessOf (HInts gap) []) |> List.minBy (function IntCase value -> value < 0L, (if value = Int64.MinValue then Int64.MaxValue else abs value) | _ -> false, 0L))
                | [ "string" ]
                | [ "float" ] ->
                    let present = heads |> List.choose (function HLit literal -> Some literal | _ -> None)
                    otherwise (LiteralCase (otherLiteral present present.Head))
                | [ "fields" ] ->
                    let names = fieldsOf []
                    through (HFields names) (names |> List.map (fun _ -> Any))
                | [ "named" ] when heads.Length = 1 -> anyOf heads
                | [ "list" ] ->
                    match heads with
                    | [ HNil ] -> otherwise (ListCons(Anything, Anything))
                    | [ HCons ] -> otherwise EmptyList
                    | _ -> anyOf [ HNil; HCons ]
                // Patterns of different kinds of value at one position, which the type checker reports.
                | _ -> otherwise Anything
            | _ ->
                match headOf first with
                | Some head -> through head (partsOf head first |> Option.defaultValue [])
                | None -> None

    /// A value that no arm of a match on values of the types `types` matches, as one witness per value, or `None` if
    /// every value is matched. An arm whose pattern count is not the values' is left out: it is an error of its own.
    /// A tag union whose type is not known yet is taken to be the tags the arms name.
    let missingCase (arms: Pattern list list) (types: Ty option list) : Witness list option =
        let rows = arms |> List.filter (fun patterns -> patterns.Length = types.Length) |> List.map (List.map (spaceOf true))
        useful true rows (List.replicate types.Length Any) types

    /// The earlier arms that between them match every value the arm with `patterns` does, so that it can never match,
    /// or `None` if they do not (or it cannot be told). `earlier` are the arms before it, with their indexes, and
    /// `types` the types of the values matched, by position, where known. A single arm that does is given when there
    /// is one; otherwise as few arms as do between them.
    let coveringArms (earlier: (int * Pattern list) list) (types: Ty option list) (patterns: Pattern list) : int list option =
        let vector = patterns |> List.map (spaceOf false)
        let types = patterns |> List.mapi (fun index _ -> types |> List.tryItem index |> Option.flatten)
        let candidates = earlier |> List.filter (fun (_, others) -> others.Length = patterns.Length)
        let cover (arms: (int * Pattern list) list) =
            (useful false (arms |> List.map (snd >> List.map (spaceOf true))) vector types).IsNone
        if not (cover candidates) then
            None
        else
            match candidates |> List.tryFind (List.singleton >> cover) with
            | Some (index, _) -> Some [ index ]
            | None ->
                candidates
                |> List.fold (fun needed arm -> let without = needed |> List.filter ((<>) arm) in if cover without then without else needed) candidates
                |> List.map fst
                |> Some

    /// `witness` as a pattern would be written. `arm` is the tags that have a payload when it is written for an arm,
    /// where such a tag is written with `(_)` and a record pattern that names only some of a record's fields ends in
    /// `...`; it is `None` when it is written for a message.
    let rec private render (arm: Set<string> option) (witness: Witness) : string =
        let show = render arm
        let asArm = arm.IsSome
        let items (witnesses: Witness list) = witnesses |> List.map show |> String.concat ", "
        match witness with
        | Anything -> "_"
        | OneOfTags tags -> tags |> List.tryHead |> Option.map (sprintf "#%s") |> Option.defaultValue "_"
        | TagCase(name, Anything) when arm |> Option.exists (Set.contains name) -> $"#{name}(_)"
        | TagCase(name, Anything) -> $"#{name}"
        | TagCase(name, payload) -> $"#{name}({show payload})"
        | BoolCase value -> if value then "true" else "false"
        | IntCase value -> string value
        | LiteralCase (StringLit text) -> $"\"{text}\""
        | LiteralCase (FloatLit value) when Math.Round value = value -> sprintf "%.1f" value
        | LiteralCase (FloatLit value) -> string value
        | LiteralCase (IntLit value) -> string value
        | LiteralCase (BoolLit value) -> show (BoolCase value)
        | RecordCase fields when fields |> List.mapi (fun index (name, _) -> name = string index) |> List.forall id ->
            $"({items (List.map snd fields)})"
        | RecordCase fields ->
            match fields |> List.filter (fun (_, part) -> part <> Anything) with
            | [] -> "_"
            | named -> "(" + (named |> List.map (fun (name, part) -> $"{name} = {show part}") |> String.concat ", ") + (if asArm then ", ...)" else ")")
        | NamedCase(name, parts) -> $"{name}({items parts})"
        | EmptyList -> "[]"
        | ListCons _ ->
            let rec elements (witness: Witness) =
                match witness with
                | ListCons(head, tail) -> let shown, rest = elements tail in show head :: shown, rest
                | EmptyList -> [], ""
                | _ -> [], "..."
            let shown, rest = elements witness
            "[" + String.concat ", " (shown @ (if rest = "" then [] else [ rest ])) + "]"

    /// `witness` as a pattern would be written in a message.
    let show (witness: Witness) : string = render None witness

    /// The tags with a payload anywhere in `ty`.
    let rec private tagsWithPayloads (ty: Ty) : Set<string> =
        match unwrap ty with
        | TyTag(name, Some payload) -> Set.add name (tagsWithPayloads payload)
        | TyUnion items
        | TyApply(_, items) -> items |> List.map tagsWithPayloads |> Set.unionMany
        | TyRecord fields
        | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.map (snd >> tagsWithPayloads) |> Set.unionMany
        | _ -> Set.empty

    /// Every way of choosing one item from each of `choices`, in order.
    let private combinations (choices: 'a list list) : 'a list list =
        List.foldBack (fun options rest -> [ for option in options do for after in rest -> option :: after ]) choices [ [] ]

    /// The witnesses an arm is written for to match every value `witness` stands for: one for each tag of a
    /// `OneOfTags`, and an int, string or float made `Anything`, since those have too many values to name one by one.
    let rec private armWitnesses (witness: Witness) : Witness list =
        match witness with
        | OneOfTags tags -> tags |> List.map (fun tag -> TagCase(tag, Anything))
        | IntCase _
        | LiteralCase _ -> [ Anything ]
        | TagCase(name, payload) -> armWitnesses payload |> List.map (fun inner -> TagCase(name, inner))
        | RecordCase fields ->
            fields
            |> List.map (fun (name, part) -> armWitnesses part |> List.map (fun inner -> name, inner))
            |> combinations
            |> List.map RecordCase
        | NamedCase(name, parts) -> parts |> List.map armWitnesses |> combinations |> List.map (fun inner -> NamedCase(name, inner))
        | ListCons(head, tail) -> [ for first in armWitnesses head do for rest in armWitnesses tail -> ListCons(first, rest) ]
        | Anything
        | BoolCase _
        | EmptyList -> [ witness ]

    /// What an arm written for `witness`, which `armWitnesses` gives, matches.
    let rec private spaceOfWitness (witness: Witness) : Space =
        match witness with
        | TagCase(name, payload) -> Tag(name, spaceOfWitness payload)
        | BoolCase value -> Bool value
        | RecordCase fields -> fields |> List.map (fun (name, part) -> name, spaceOfWitness part) |> Map.ofList |> Fields
        | NamedCase(name, parts) -> Named(name, parts |> List.map spaceOfWitness)
        | EmptyList -> Nil
        | ListCons(head, tail) -> Cons(spaceOfWitness head, spaceOfWitness tail)
        | Anything
        | OneOfTags _
        | IntCase _
        | LiteralCase _ -> Any

    /// The most arms `missingArms` offers to add; a match that needs more gets none.
    let private mostMissingArms = 12

    /// The arms to add to a match on values of the types `types`, whose arms have the patterns `arms`, so that it
    /// matches every value: the patterns of each, as they are written, one per value. Empty if it already does, or if
    /// it would take more than a dozen arms.
    let missingArms (arms: Pattern list list) (types: Ty option list) : string list list =
        let rec added (rows: Space list list) (found: Witness list list) =
            match useful true rows (List.replicate types.Length Any) types with
            | _ when found.Length > mostMissingArms -> []
            | None -> List.rev found
            | Some witnesses ->
                let next = witnesses |> List.map armWitnesses |> combinations
                added (rows @ (next |> List.map (List.map spaceOfWitness))) (List.rev next @ found)
        let rows = arms |> List.filter (fun patterns -> patterns.Length = types.Length) |> List.map (List.map (spaceOf true))
        let payloads = types |> List.choose id |> List.map tagsWithPayloads |> Set.unionMany
        added rows [] |> List.map (List.map (render (Some payloads)))

    /// What a match is missing at one value, when `witness` is a value no arm matches there and every other value
    /// may be anything: the tags no arm names, list or record cases, or a value of a type with too many to name.
    let whatIsMissing (witness: Witness) : string =
        match witness with
        | OneOfTags tags -> $"""missing tags: {String.concat ", " tags}"""
        | EmptyList
        | ListCons _ -> "missing list cases"
        | RecordCase fields when not (fields |> List.mapi (fun index (name, _) -> name = string index) |> List.forall id) -> "missing record cases"
        | NamedCase _ -> "missing record cases"
        | BoolCase _ -> $"missing value: {show witness}"
        | IntCase _ -> $"missing int values, such as {show witness}"
        | LiteralCase (StringLit _) -> $"missing string values, such as {show witness}"
        | LiteralCase _ -> $"missing float values, such as {show witness}"
        | _ -> $"missing case: {show witness}"
