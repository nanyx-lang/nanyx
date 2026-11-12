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
    /// contexts its own type requires, which each call passes it.
    | LiftedLambda of Expression * string list * owner: string
    /// What lets the top-level function of this name be passed as a value: it takes an environment and ignores it.
    | FunctionWrapper of string

/// A member of a context a function requires, which it takes as a parameter of its own after its others.
type ContextParameter =
    { /// The member's key (see `Identifiers.contextMemberKey`): its name, or `<context key>_<name>` for a member of a
      /// parameterised context.
      Key: string
      /// The parameter's name in WebAssembly text.
      Name: string
      /// The member's type, in the function's own type variables; none when it cannot be worked out from the
      /// annotation.
      Type: Ty option }

/// Where a call finds a member of a context its callee requires.
type ContextArgument =
    /// The member in scope under this key: a parameter of the function, or a `use` binding.
    | MemberInScope of string
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

/// What the lowering reads from the typed module.
type LowerInput =
    { /// The module's top-level definitions as the backend compiles them, in order: a function is a lambda.
      Definitions: (string * Expression) list
      /// The type of each expression, and of the function at each call, by node (`TypedModule.ExprTypes` and
      /// `CalleeTypes`). Type variables in them are the generalised variables of the definition they are in.
      ExprTypes: Map<NodeId, Ty>
      CalleeTypes: Map<NodeId, Ty>
      /// Each definition's type, generalised (`TypedModule.Types`).
      DefinitionTypes: Map<string, Ty>
      /// The context members each definition that requires contexts takes, in order, and the other names it can call
      /// them by (a member of a parameterised context, by its own name, when no other context it requires has one of
      /// that name).
      ContextParameters: Map<string, ContextParameter list * Map<string, string>>
      /// Where a call to the definition of this name, at this node, finds each context member it requires, given the
      /// keys of the members in scope there and of those its callers pass instead (`PassedByCaller`); or why it cannot.
      ContextArguments: string -> NodeId -> Set<string> -> Set<string> -> Result<ContextArgument list, string>
      /// The members of each context without parameters (see `CoreIrLowerTypes.ContextLayouts`).
      ContextLayouts: Map<string, (string * Ty) list>
      /// The wrapper of the ambient instance's member of each key (the runtime's `Console`), which stands in for it where
      /// nothing in scope supplies it.
      AmbientMembers: Map<string, string>
      /// The calls of interpolation holes that use the compiler's own `ToString` (see `InterpolationHoles`), by node: the
      /// value is written into the string as it is.
      BuiltInHoles: Set<NodeId>
      /// The record types the module defines, whose names construct a record: `Person(name = "Ada", age = 36)`.
      RecordConstructors: Set<string>
      /// The nominal types whose shape is not a record (`type $Money = private int`). A value of one is its shape's
      /// value, so its constructor and the pattern that takes it apart are no-ops.
      NominalWrappers: Set<string>
      /// The functions lambda lifting made, by name.
      Lifted: Map<string, LiftedFunction>
      /// The module's top-level expressions, which its start function evaluates in order when the module is loaded.
      TopLevel: Expression list
      /// The types the module defines (records and nominal types, not contexts), by name: what an annotation that
      /// names one means.
      NamedTypes: Map<string, Ty> }

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
      /// The specialisations of each generic definition, by the definition's name, in the order they were asked for.
      Specialisations: Map<string, (Function * 'T) list>
      /// What could not be lowered, in the order it was met: the function (by its definition's name), why, and where.
      Failures: (string * LowerFailure * SourceSpan option) list }

