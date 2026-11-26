/// What the lowering to the core IR (see `CoreIrLower`) reads from the typed module, and what it gives back.
module Transpiler.Wasm.CoreIrLowerInput

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CoreIr

/// A function lambda lifting made (see `LambdaLifting.fs`), which the IR lowers where a closure over it is made.
type LiftedFunction =
    /// A lambda, or a `rec` local function, lifted out of a definition: the lambda as written (which the type checker
    /// typed), the names it captures, in the order its environment holds them, and the definition it was lifted out of.
    /// It captures that definition's context members under the names of the parameters they come in, but those of the
    /// contexts its own type requires, which each call passes it. Those of a local function written with its type are
    /// the contexts the type writes, as written (see `LambdaLifting.LiftedOrigin`).
    | LiftedLambda of Expression * string list * owner: string * written: TypeExpr list
    /// What lets the top-level function of this name be passed as a value: it takes an environment and ignores it.
    | FunctionWrapper of string

/// A member of a context a function requires, which it takes as a parameter of its own after its others.
type ContextParameter =
    { /// The member's key (see `Identifiers.contextMemberKey`).
      Key: ContextMemberKey
      /// The parameter's name in WebAssembly text.
      Name: string
      /// The member's type, in the function's own type variables; none when it cannot be worked out from the
      /// annotation.
      Type: Ty option }

/// Where a call finds a member of a context its callee requires.
type ContextArgument =
    /// The member in scope under this key: a parameter of the function, or a `use` binding.
    | MemberInScope of ContextMemberKey
    /// A closure over this wrapper of a top-level function: a member of the ambient instance (the runtime's
    /// `Console`), or of an instance the compiler built from attached definitions.
    | ClosureOverWrapper of string
    /// The value of this top-level definition.
    | DefinitionValue of string
    /// A closure over this wrapper of a definition the compiler derived from the parts of a type (see
    /// `InstanceDerived`), which captures the members of the contexts the definition requires, each found where the
    /// type checker found it for a call to the definition at this node.
    | DerivedClosure of wrapper: string * at: NodeId
    /// None where the function is named: a member of a context its value requires where it is called, which each call
    /// passes (see `IrFunction`).
    | PassedByCaller
    /// Nowhere: the type checker found no instance of the member's context, and nothing in scope supplies the member of
    /// this key. That is only right for a member at a type nothing constrains, which is never called (see
    /// `CoreIrLowerInstances.standInType`).
    | Unfound of ContextMemberKey

/// What a call to `dbg` compiles to.
type DbgBuild =
    /// A debug build's: it prints the value after where it was called, `[main.nyx:12] `, with the file named from this
    /// directory, the package's root (see `PackageModules.shownFile`).
    | PrintedFrom of root: string option
    /// A release build's: `dbg(x)` is `x`, which prints nothing.
    | Stripped

module DbgBuild =
    /// The root a debug build names its files from; none for a release build, which names none.
    let sourceRoot (build: DbgBuild) =
        match build with
        | PrintedFrom root -> root
        | Stripped -> None

/// What the lowering reads from the typed module.
type LowerInput =
    { /// The module's top-level definitions as the backend compiles them, in order: a function is a lambda.
      Definitions: (string * Expression) list
      /// Where each top-level definition is written, its `def` and all, by name: a profile gives a function the line its
      /// `def` starts on (see `CoreIr.Function.Place`), which its lambda may start after.
      DefinitionSpans: Map<string, SourceSpan>
      /// The foreign functions (see `ExternSyntax`), by name: a call to one is a call to the module's import of it, and they
      /// are not among `Definitions`.
      Externs: Map<string, NyxCompiler.Extern>
      /// The type of each expression, and of the function at each call, by node (`TypedModule.ExprTypes` and
      /// `CalleeTypes`). Type variables in them are the generalised variables of the definition they are in.
      ExprTypes: Map<NodeId, Ty>
      CalleeTypes: Map<NodeId, Ty>
      /// What the associated types of the contexts each call's callee requires are at the call, by the names the callee's
      /// signature gives them (`TypedModule.AssociatedArguments`): its type need not write them, so they are bound apart.
      AssociatedArguments: Map<NodeId, Map<string, Ty>>
      /// Each definition's type, generalised (`TypedModule.Types`).
      DefinitionTypes: Map<string, Ty>
      /// The context members each definition that requires contexts takes, in order, and the other names it can call
      /// them by (a member of a parameterised context, by its own name, when no other context it requires has one of
      /// that name).
      ContextParameters: Map<string, ContextParameter list * Map<string, ContextMemberKey>>
      /// Where a call to the definition of this name, at this node, finds each context member it requires, given the
      /// keys of the members in scope there and of those its callers pass instead (`PassedByCaller`); or why it cannot.
      ContextArguments: string -> NodeId -> Set<ContextMemberKey> -> Set<ContextMemberKey> -> Result<ContextArgument list, string>
      /// Where a call through a function value, at this node, finds the member of this name of the context at this place
      /// among those the value's type writes (see `LocalCallees`), given the keys of the members in scope there:
      /// the key of a member in scope, or else where else it is, where the type checker found the context at the call.
      FoundMember: NodeId -> int -> string -> Set<ContextMemberKey> -> Choice<ContextMemberKey, ContextArgument> option
      /// Each name that refers to a local function written with a type that requires parameterised contexts, by its
      /// node: the contexts the type writes, in order, and the type of the function's lambda, both in the lambda's own
      /// type variables. The lambda's type in `ExprTypes` requires the parameterised ones.
      LocalCallees: Map<NodeId, Ty list * Ty>
      /// The members of each context (see `ContextLayouts`).
      ContextLayouts: ContextLayouts
      /// The wrapper of the ambient instance's member of each key (the runtime's `Console`), which stands in for it where
      /// nothing in scope supplies it.
      AmbientMembers: Map<ContextMemberKey, string>
      /// The key of each name a `use` lambda lifting left behind binds a member of a parameterised context by (see
      /// `LambdaLifting.lift`). Any other name a `use` binds is a named member's.
      UseKeys: Map<string, ContextMemberKey>
      /// The plain context each local a `use` binding is evaluated into binds a member of, where the `use` names it
      /// (`use Proc(...)`), by the local's name (see `LambdaLifting.lift`).
      UseBindingContexts: Map<string, string>
      /// The calls of interpolation holes that use the compiler's own `ToString` (see `InterpolationHoles`), by node: the
      /// value is written into the string as it is.
      BuiltInHoles: Set<NodeId>
      /// The names written in the module that refer to a top-level def other than through an import, by node, with where
      /// the def is declared. One declared in the file the name is written in is the module's own, which wins over a
      /// context member of the same name in scope, as `def write = ...` in a script that runs in `Console` does.
      TopLevelReferences: Map<NodeId, SourceSpan>
      /// The record types the module defines, whose names construct a record: `Person(name = "Ada", age = 36)`.
      RecordConstructors: Set<string>
      /// The types the module defines whose shape is not a record: nominal (`type Money = private int`) or not
      /// (`type Pair = (int, int)`). A value of one is its shape's value, so its constructor is a no-op, and so is the
      /// pattern that takes a nominal one apart.
      ShapeWrappers: Set<string>
      /// The functions lambda lifting made, by name.
      Lifted: Map<string, LiftedFunction>
      /// The module's top-level expressions, which its start function evaluates in order when the module is loaded.
      TopLevel: Expression list
      /// The types the module defines (records and nominal types, not contexts), by name: what an annotation that
      /// names one means.
      NamedTypes: Map<string, Ty>
      /// What a call to `dbg` compiles to.
      Dbg: DbgBuild
      /// Whether the module has debug probes (see `CoreIrProbes`).
      Probes: bool
      /// The numbers the types `Type` values stand for are at run time (see `CoreIrTypeIds`).
      TypeIds: CoreIrTypeIds.TypeIds
      /// The type definitions in scope, by name: what a type's description says of it besides its structure, its doc
      /// comment and the attributes on its fields (see `CoreIrLowerTypeMatch`).
      TypeDefinitions: Map<string, TypeDefInfo> }

/// The top-level function `wrapper`, a wrapper lambda lifting made, wraps.
let wrappedOf (input: LowerInput) (wrapper: string) =
    match input.Lifted |> Map.tryFind wrapper with
    | Some (FunctionWrapper wrapped) -> wrapped
    | _ -> internalError $"'{wrapper}' is not a function's wrapper"

/// Whether `expr`, a name, refers to a top-level def of the module it is written in, which wins over a context member of
/// that name in scope (see `LowerInput.TopLevelReferences`).
let namesOwnDefinition (input: LowerInput) (expr: Expression) =
    input.TopLevelReferences |> Map.tryFind expr.Id |> Option.exists (fun declared -> declared.File = expr.Span.File)

/// The name of the start function, which evaluates the module's top-level expressions.
let startFunctionName = "__nanyx_start"

/// Why a function could not be lowered or finished.
type LowerFailure =
    /// Something the backend does not compile yet.
    | NotYet of string
    /// A broken invariant of the backend (see `InternalError`).
    | Internal of string

/// A function the IR lowered, and what it was finished into (see `CoreIrLowerModule.lowerModule`).
type Lowered<'T> =
    { /// The definitions lowered as they are, by name.
      Roots: Map<string, Function * 'T>
      /// The specialisations of each definition, by the definition's name, in the order they were asked for: of a
      /// generic definition at type arguments, and of a definition that requires contexts on known members (see
      /// `CoreIrLowerKnown`).
      Specialisations: Map<string, (Function * 'T) list>
      /// What could not be lowered, in the order it was met: the function (by its definition's name), why, and where.
      Failures: (string * LowerFailure * SourceSpan option) list }

