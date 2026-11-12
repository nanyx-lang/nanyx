namespace NyxCompiler

open Parser.Program

type ConstraintKind =
    | Equal
    | Assignable
    /// An `Assignable` constraint the unifier has put off until the constraints after it are solved, because what one
    /// of its tag unions has is not known yet (see `Unifier.unify`). It is not put off again.
    | AssignableLater

/// A constraint between two types, with the span of the code that asked for it: where a failure to satisfy it is
/// reported.
type Constraint = Ty * Ty * ConstraintKind * SourceSpan

type ConstraintSet = Constraint list

module Constraints =
    let empty : ConstraintSet = []

    let add constraint' (constraints: ConstraintSet) =
        constraint' :: constraints
