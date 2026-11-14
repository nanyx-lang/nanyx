/// How each function the lowering to the core IR makes is lowered, and what names refer to in the expression being
/// lowered (see `CoreIrLower`).
module internal Transpiler.Wasm.CoreIrLowerScope

open NyxCompiler
open Transpiler.Wasm.CoreIr

/// How a function is lowered.
type internal Lowering =
    /// As its definition says.
    | AsDefined
    /// As a lifted lambda whose closure captures values of these types, and these captured locals generic in type
    /// variables of their own (see `Scope.CapturedLocals`).
    | LiftedCapturing of IrType list * Map<string, Ty * (IrType list * int) list>
    /// As the wrapper of a top-level function, which it calls at this type, with the members of the contexts it
    /// requires: those of these keys and types, which the closure captures, and those its value's type requires, which
    /// each call passes.
    | WrapperCalling of string * Ty * (ContextMemberKey * IrType) list
    /// As a function value of the second type that calls one of the first, which its closure holds, with the members
    /// the first requires: each passed to it, or captured after the value where it was made (see `Coerce`).
    | Adapting of IrType * IrType
    /// As the function a box of a value of this type is a closure over (see `CoreIrLowerBoxes`).
    | BoxReading of IrType
    /// As a function that writes values as text for `dbg` (see `CoreIrLowerDbg`).
    | Printing of CoreIrLowerDbg.Printer
    /// As the start function, from the module's top-level expressions.
    | AsStart

/// Lowers the closure of a local function generic in type variables of its own at some type arguments, given what they
/// bind, and passes it on.
type internal LocalClosure = Map<int, Ty> -> IrType list -> (Expr -> Expr) -> Expr

/// What names refer to in the expression being lowered.
type internal Scope =
    { Vars: Map<string, Var>
      /// How many scrutinees enclose the expression: the next `match` holds its own from this number on.
      MatchDepth: int
      /// The names of the pattern-bound locals of the arms that enclose the expression, which a nested arm must not
      /// reuse.
      LivePayloads: Set<string>
      /// How many interpolated strings' holes enclose the expression (see `Interpolation.holeLocalName`).
      InterpolationDepth: int
      /// The context members in scope, by key (and by the other names they can be called by): each a variable holding
      /// the member's closure.
      Members: Map<ContextMemberKey, Var>
      /// In a lifted lambda, the captured locals that hold context members (`__ctx_<key>`), each with the keys and other
      /// names the member is in scope by once the local is bound.
      CapturedMembers: Map<string, ContextMemberKey list>
      /// How many loops enclose the expression, which names a `for` loop's locals and a `while` loop's labels.
      ForDepth: int
      /// The closures of each local function generic in type variables of its own, by the variable its name is bound
      /// to: its type, a closure for each type arguments it is used at (see `Block`), and how to make it at others where
      /// that can be done.
      LocalInstances: Map<int, Ty * (IrType list * Var) list * LocalClosure option>
      /// In a lifted lambda, the captured locals that hold a local function generic in type variables of its own: its
      /// type, and the field of the environment that holds its closure at each type arguments it is used at here.
      CapturedLocals: Map<string, Ty * (IrType list * int) list> }
