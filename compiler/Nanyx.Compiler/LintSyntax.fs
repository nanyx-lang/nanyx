/// A module's syntax tree as the data a linter written in Nanyx is given: `nanyx.analysis/syntax`'s types, by the names of
/// their fields and tags (see `LintData`). A change to a node here is a change to that module, and the other way round.
module internal NyxCompiler.LintSyntax

open Parser.Program
open NyxCompiler.LintData

/// `Span`: offsets into the module's text and the line and column of its start, -1 for a node the compiler made up.
let span (span: SourceSpan) =
    DRecord [ "start", DInt(int64 span.Start); "end", DInt(int64 span.End); "line", DInt(int64 span.StartLine); "column", DInt(int64 span.StartColumn) ]

let private text (value: string) = DText value
let private optional (encode: 'a -> LintData) (value: 'a option) = option "none" (Option.map encode value)
let private node (id: NodeId) (at: SourceSpan) (kind: LintData) = DRecord [ "id", DInt id; "span", span at; "kind", kind ]

let private literal (value: Literal) =
    match value with
    | StringLit s -> DTag("string", text s)
    | IntLit n when n >= bigint System.Int64.MinValue && n <= bigint System.Int64.MaxValue -> DTag("int", DInt(int64 n))
    // An int too large for an `int` is written as it is, a measure with no unit.
    | IntLit n -> DTag("measure", DRecord [ "number", text (string n); "unit", text "" ])
    | FloatLit x -> DTag("float", DFloat x)
    | BoolLit b -> DTag("bool", DBool b)
    | UnitLit(number, unit) -> DTag("measure", DRecord [ "number", text (WrittenNumber.text number); "unit", text unit ])

let private import (item: ImportItem) =
    DRecord
        [ "source",
          (match item.Source with
           | ModuleImport name -> DTag("module", text name)
           | PathImport path -> DTag("path", text path))
          "alias", optional text item.Alias
          "selection", optional (List.map (fun (name, at) -> tuple [ text name; span at ]) >> DList) item.Selection
          "span", span item.Span ]

let private modifier (value: TypeDefModifier) =
    match value with
    | Export -> tag "export"
    | Context -> tag "context"
    | Private -> tag "private"
    | Protected -> tag "protected"
    | Scoped at -> DTag("scoped", span at)
    | InScopeOf contexts -> DTag("inScopeOf", DList(contexts |> List.map (fun (name, at) -> tuple [ text name; span at ])))
    | KindAt at -> DTag("kindAt", span at)
    | AssociatedTypes names -> DTag("associatedTypes", DList(List.map text names))

let rec private typeExpr (written: TypeExpr) : LintData =
    let kind =
        match written.Kind with
        | TypeName name -> DTag("name", text name)
        | TypeVar name -> DTag("variable", text name)
        | TypeHole -> tag "hole"
        | TypeUnit -> tag "unit"
        | TypeTuple items -> DTag("tuple", DList(List.map typeExpr items))
        | TypeRecord fields -> DTag("record", DList(List.map typeField fields))
        | TypeOpenRecord(fields, rest) -> DTag("openRecord", DRecord [ "fields", DList(List.map typeField fields); "rest", optional text rest ])
        | TypeApply(name, args) -> DTag("apply", DRecord [ "name", text name; "arguments", DList(List.map typeExpr args) ])
        | TypeFunc(input, output) -> DTag("function", DRecord [ "input", typeExpr input; "output", typeExpr output ])
        | TypeTag(name, payload) -> DTag("tag", DRecord [ "name", text name; "payload", optional typeExpr payload ])
        | TypeUnion items -> DTag("union", DList(List.map typeExpr items))
        | TypeIntersection items -> DTag("intersection", DList(List.map typeExpr items))
        | TypeContext items -> DTag("contexts", DList(List.map typeExpr items))
        | TypeWithContext(contexts, inner) -> DTag("withContexts", DRecord [ "contexts", DList(List.map typeExpr contexts); "inner", typeExpr inner ])
        | TypeConstraint(name, inner, predicate) -> DTag("constraint", DRecord [ "name", text name; "inner", typeExpr inner; "predicate", expr predicate ])
        | TypeWhere(inner, predicate) -> DTag("where", DRecord [ "inner", typeExpr inner; "predicate", expr predicate ])
        | TypeOf value -> DTag("typeOf", expr value)
        | TypeKeyword -> tag "typeKeyword"
        | TypeNamedArgument(name, value) -> DTag("namedArgument", DRecord [ "name", text name; "value", typeExpr value ])
        | TypeAssociated(context, name) -> DTag("associated", DRecord [ "context", typeExpr context; "name", text name ])
        | TypeArgument inner -> DTag("typeArgument", typeExpr inner)
        | TypeProduct(left, right) -> DTag("product", tuple [ typeExpr left; typeExpr right ])
        | TypeQuotient(left, right) -> DTag("quotient", tuple [ typeExpr left; typeExpr right ])
    node written.Id written.Span kind

and private typeField (field: TypeRecordField) =
    match field with
    | TypeField(name, isMutable, annotation, defaultValue) ->
        DTag("field", DRecord [ "name", text name; "isMutable", DBool isMutable; "annotation", optional typeExpr annotation; "default", optional expr defaultValue ])
    | TypeFieldAttribute(name, argument, at) -> DTag("attribute", DRecord [ "name", text name; "argument", optional expr argument; "span", span at ])

and private parameter (name: Identifier, annotation: TypeExpr option) = tuple [ text name; optional typeExpr annotation ]

and private typeDefinition (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    DRecord [ "name", text name; "modifiers", DList(List.map modifier modifiers); "parameters", DList(List.map parameter parameters); "body", typeExpr body ]

and private definition (isExport: bool) (name: Identifier) (annotation: TypeExpr option) (value: Expression) =
    DRecord [ "isExport", DBool isExport; "name", text name; "annotation", optional typeExpr annotation; "value", expr value ]

and private pattern (written: Pattern) : LintData =
    let kind =
        match written.Kind with
        | LiteralPattern value -> DTag("literal", literal value)
        | IdentifierPattern name -> DTag("name", text name)
        | WildcardPattern -> tag "wildcard"
        | UnitPattern -> tag "unit"
        | ElsePattern -> tag "else"
        | TuplePattern(items, hasRest) -> DTag("tuple", DRecord [ "items", DList(List.map pattern items); "hasRest", DBool hasRest ])
        | RecordPattern(name, patterns) -> DTag("constructor", DRecord [ "name", text name; "patterns", DList(List.map pattern patterns) ])
        | RecordMemberPattern(members, hasRest) ->
            DTag("members", DRecord [ "members", DList(members |> List.map (fun (name, p) -> tuple [ text name; pattern p ])); "hasRest", DBool hasRest ])
        | ListPattern(items, rest) -> DTag("list", DRecord [ "items", DList(List.map pattern items); "rest", optional pattern rest ])
        | ListSplatMiddle(before, after) -> DTag("listMiddle", DRecord [ "before", DList(List.map pattern before); "after", DList(List.map pattern after) ])
        | RangePattern(from, until) -> DTag("range", DRecord [ "from", expr from; "to", expr until ])
        | GuardPattern predicate -> DTag("guard", expr predicate)
        | TagPattern(name, payload) -> DTag("tag", DRecord [ "name", text name; "payload", optional pattern payload ])
        | ArgumentTagPattern(name, arguments, payload) ->
            DTag("argumentTag", DRecord [ "name", text name; "arguments", expr arguments; "payload", optional pattern payload ])
        | AndPattern items -> DTag("and", DList(List.map pattern items))
    node written.Id written.Span kind

and private statement (written: Statement) : LintData =
    let kind =
        match written.Kind with
        | DefStatement(isExport, name, annotation, value) -> DTag("def", definition isExport name annotation value)
        | TypeDefStatement(name, modifiers, parameters, body) -> DTag("typeDef", typeDefinition name modifiers parameters body)
        | ImportStatement items -> DTag("import", DList(List.map import items))
        | UseStatement value -> DTag("use", expr value)
        | ExprStatement value -> DTag("expr", expr value)
        | AttributeStatement(name, argument) -> DTag("attribute", DRecord [ "name", text name; "argument", optional expr argument ])
    node written.Id written.Span kind

and private arm (patterns: Pattern list, body: Expression) = DRecord [ "patterns", DList(List.map pattern patterns); "body", expr body ]

and private expr (written: Expression) : LintData =
    let exprs = List.map expr >> DList
    let kind =
        match written.Kind with
        | LiteralExpr value -> DTag("literal", literal value)
        | UnitExpr -> tag "unit"
        | IdentifierExpr name -> DTag("name", text name)
        | FunctionCall(name, nameSpan, args) -> DTag("call", DRecord [ "name", text name; "nameSpan", optional span nameSpan; "arguments", exprs args ])
        | Lambda(parameters, body, takesUnit) ->
            DTag("lambda", DRecord [ "parameters", DList(List.map parameter parameters); "body", expr body; "takesUnit", DBool takesUnit ])
        | ContextNames(names, body) ->
            let named (name, context, at) = tuple [ text name; optional typeExpr context; span at ]
            DTag("contextNames", DRecord [ "names", DList(List.map named names); "body", expr body ])
        | BinaryOp(operator, operatorSpan, left, right) ->
            DTag("binary", DRecord [ "operator", text operator; "operatorSpan", optional span operatorSpan; "left", expr left; "right", expr right ])
        | Pipe(value, name, nameSpan, args) ->
            DTag("pipe", DRecord [ "value", expr value; "name", text name; "nameSpan", optional span nameSpan; "arguments", exprs args ])
        | Block statements -> DTag("block", DList(List.map statement statements))
        | Match(values, arms) -> DTag("match", DRecord [ "values", exprs values; "arms", DList(List.map arm arms) ])
        | Except(value, arms) -> DTag("except", DRecord [ "value", expr value; "arms", DList(List.map arm arms) ])
        | TupleExpr items -> DTag("tuple", exprs items)
        | RecordExpr fields ->
            let field =
                function
                | NamedField(name, value) -> DTag("named", tuple [ text name; expr value ])
                | PositionalField value -> DTag("positional", expr value)
            DTag("record", DList(List.map field fields))
        | ListExpr items -> DTag("list", exprs items)
        | TagExpr(name, payload) -> DTag("tag", DRecord [ "name", text name; "payload", optional expr payload ])
        | IfExpr(condition, whenTrue, whenFalse) ->
            DTag("if", DRecord [ "condition", expr condition; "whenTrue", expr whenTrue; "whenFalse", expr whenFalse ])
        | MemberAccess(target, field, fieldSpan) -> DTag("member", DRecord [ "target", expr target; "field", text field; "fieldSpan", optional span fieldSpan ])
        | ContextMemberAccess(context, member') -> DTag("contextMember", DRecord [ "context", typeExpr context; "member", text member' ])
        | ContextMemberCall(context, member', args) ->
            DTag("contextCall", DRecord [ "context", typeExpr context; "member", text member'; "arguments", exprs args ])
        | TypeValue written -> DTag("typeValue", typeExpr written)
        | UseIn(value, body) -> DTag("useIn", DRecord [ "value", expr value; "body", expr body ])
        | WorkflowBindExpr(keyword, value) -> DTag("bind", DRecord [ "keyword", text keyword; "value", expr value ])
        | WorkflowReturnExpr value -> DTag("return", expr value)
        | InterpolatedString parts ->
            let part =
                function
                | StringText s -> DTag("text", text s)
                | StringExpr value -> DTag("expr", expr value)
            DTag("interpolated", DList(List.map part parts))
    node written.Id written.Span kind

let rec private item (written: TopLevelItem) : LintData =
    let kind =
        match written.Kind with
        | ModuleDecl(isExport, name) -> DTag("moduleDeclaration", DRecord [ "isExport", DBool isExport; "name", text name ])
        | ModuleBlock(name, items) -> DTag("moduleBlock", DRecord [ "name", text name; "items", DList(List.map item items) ])
        | Def(ValueDef(isExport, name, annotation, value)) -> DTag("def", definition isExport name annotation value)
        | Def(TypeDef(name, modifiers, parameters, body)) -> DTag("typeDef", typeDefinition name modifiers parameters body)
        | Import items -> DTag("import", DList(List.map import items))
        | ReExport imported -> DTag("reExport", import imported)
        | Expr value -> DTag("expr", expr value)
        | Attribute(name, argument) -> DTag("attribute", DRecord [ "name", text name; "argument", optional expr argument ])
        | DimensionDecl(isExport, name, storage, derived) ->
            DTag("dimension", DRecord [ "isExport", DBool isExport; "name", text name; "storage", optional typeExpr storage; "derived", optional typeExpr derived ])
        | UnitDecl(isExport, name, dimension, multiple) ->
            DTag("unitDeclaration", DRecord [ "isExport", DBool isExport; "name", text name; "dimension", typeExpr dimension; "multiple", optional expr multiple ])
    node written.Id written.Span kind

/// `Module`: the module's top-level items, as written.
let ofModule (syntax: Module) : LintData = DList(List.map item syntax)
