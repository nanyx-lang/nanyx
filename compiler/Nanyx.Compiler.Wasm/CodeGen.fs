module Transpiler.Wasm.CodeGen

open System
open System.Text
open NyxCompiler
open Parser.Program

/// Something the backend cannot compile, and where it is written: what `tryTranspileTypedModuleToWat` reports as a
/// diagnostic. A failure anywhere in the backend becomes one of these at the innermost expression it happened in that
/// has a place in the source (see `locatedAt`).
exception UnsupportedConstruct of message: string * span: SourceSpan

/// Fails with `message`, placed at `span`.
let private unsupportedAt (span: SourceSpan) (message: string) : 'a = raise (UnsupportedConstruct(message, span))

/// Runs `action`. A failure that does not say where it happened yet is placed at `span`, when that is a place in the
/// source; one the compiler made up has none, so the failure is left for an enclosing expression to place.
let private locatedAt (span: SourceSpan) (action: unit -> 'a) : 'a =
    try
        action ()
    with
    | UnsupportedConstruct _ -> reraise ()
    | Failure message when not (SourceSpan.isNone span) -> unsupportedAt span message
    | ex when not (SourceSpan.isNone span) -> unsupportedAt span $"Internal error: {ex.Message}"

type private ContextFn = string * int * int

/// The module's function table (`$ctx_table`), as far as functions used as values are concerned: a top-level
/// function referenced without being called, or a lifted lambda, gets a slot here and is called through
/// `call_indirect`. Shared by every function of a module and read when the table is rendered.
type private FunctionTable =
    { /// Function name -> (WebAssembly name, slot, arity).
      Values: System.Collections.Generic.Dictionary<string, ContextFn>
      /// The next free slot.
      mutable NextSlot: int
      /// The arities of every indirect call made through a local, so a `$ctx_fn_N` type exists for each.
      IndirectArities: System.Collections.Generic.HashSet<int> }

/// The record layouts the module allocates and the fields it reads from open records (whose layout is only known
/// at run time), each numbered as the functions are lowered. Every record carries its layout's number, and the
/// open-field helper is generated from both tables once every function has been lowered.
type private RecordTable =
    { /// Field names in storage order -> the layout's number.
      LayoutIds: System.Collections.Generic.Dictionary<string list, int>
      /// Field name -> its number, for the fields read from open records.
      OpenFieldIds: System.Collections.Generic.Dictionary<string, int> }

type private TranspileEnv =
    { KnownFunctions: Set<string>
      FunctionArities: Map<string, int>
      /// The parameter names of every top-level function, which a record argument's named fields are matched to.
      FunctionParameters: Map<string, string list>
      FunctionContextRequirements: Map<string, string list>
      /// The simple result type ("string", "int" or "bool") of the functions whose annotation or inferred type says.
      FunctionReturnTypes: Map<string, string>
      Locals: Map<string, string>
      DbgTempLocal: string option
      TagIds: Map<string, int>
      MatchTempNames: string list
      NextMatchTemp: int
      StringLiterals: Map<string, int>
      /// The context members in scope, by key (see `contextMemberKey`): each is a function value,
      /// a closure block, and this is the code that pushes it. A function that requires a context gets one
      /// parameter per member; a `use` binds members to the locals it evaluated its bindings into.
      ContextMembers: Map<string, string>
      /// Module-level functions that stand in for a context member of the same name when a callee needs a
      /// context and nothing in scope supplies it: the ambient instance (e.g. the runtime's `Console`). Maps
      /// the member to the wrapper that lets the function be called as a function value.
      AmbientContextMembers: Map<string, string>
      RecordLayouts: Map<string, string list>
      TypeLayouts: Map<string, string list>
      /// The nominal types of the module whose shape is not a record (`type $Money = private int`). A value of one
      /// is its shape's value as it is, so its constructor and the pattern that takes it apart compile to nothing.
      NominalWrappers: Set<string>
      /// Where each call to a function that requires a parameterised context finds its instances, by the call's
      /// node, as the type checker decided (see `TypedModule.ContextInstances`).
      ContextInstances: Map<NodeId, ContextInstance option list>
      /// The contexts each top-level function's type declares, in order.
      FunctionContextTypes: Map<string, TypeExpr list>
      /// The wrapper that lets each top-level function that has one be passed as a function value.
      FunctionWrappers: Map<string, string>
      TypeFieldTypes: Map<string, Map<string, string>>
      RecordFieldTypes: Map<string, Map<string, string>>
      ParamTypes: Map<string, string>
      ValueTypes: Map<string, string>
      /// The full inferred types of top-level defs, parameters and locals, when the module was type-checked.
      /// Record layouts are derived from these, so field access works on any expression whose type is known,
      /// not only on locals the backend saw being assigned a record literal.
      ValueTys: Map<string, Ty>
      /// The runtime helpers (see `runtimeHelpers`) the lowered code calls; shared by every function of a module.
      UsedHelpers: System.Collections.Generic.HashSet<string>
      FunctionTable: FunctionTable
      RecordTable: RecordTable
      /// How many `for` loops enclose the expression being lowered; loop locals are named by this depth.
      ForDepth: int
      /// The calls in tail position in the function being lowered (see `TailPosition.callsIn`), which are lowered
      /// to `return_call` so that they use no stack.
      TailCalls: Set<NodeId> }

/// The names of the WebAssembly helper functions the backend emits on demand (see `runtimeHelpers`).
module private Helpers =
    let alloc = "__nanyx_alloc"
    let copyBytes = "__nanyx_copy_bytes"
    let stringAlloc = "__nanyx_string_alloc"
    let stringConcat = "__nanyx_string_concat"
    let stringEquals = "__nanyx_string_equals"
    let listLength = "__nanyx_list_length"
    let listConcat = "__nanyx_list_concat"
    let recordSet = "__nanyx_record_set"
    let recordCopy = "__nanyx_record_copy"
    /// Generated per module rather than kept in `runtimeHelpers`: see `renderOpenFieldHelper`.
    let openField = "__nanyx_open_field"
    let tagBox = "__nanyx_tag_box"
    let tagCode = "__nanyx_tag_code"
    let tagEquals = "__nanyx_tag_equals"
    let floatBox = "__nanyx_float_box"
    let floatToString = "__nanyx_float_to_string"
    let writeStdout = NyxCompiler.Intrinsics.writeStdout
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let readStdinByte = NyxCompiler.Intrinsics.readStdinByte
    let stringLength = NyxCompiler.Intrinsics.stringLength
    let stringByteAt = NyxCompiler.Intrinsics.stringByteAt
    let stringSlice = NyxCompiler.Intrinsics.stringSlice
    let stringFromByte = NyxCompiler.Intrinsics.stringFromByte
    let listCons = NyxCompiler.Intrinsics.listCons

/// The WebAssembly helper functions, keyed by name. The ones named after a compiler intrinsic implement
/// that intrinsic; the rest back language operators (`+` and `==` on strings, list literals and patterns)
/// and the allocator they share. Only the helpers a module uses are emitted.
///
/// Memory model: a string is a pointer to its UTF-8 bytes, with its byte length in the 4 bytes before the
/// pointer and a 0 byte after; a list is 0 (empty) or a pointer to an 8-byte cell of (head, tail); a record (or
/// tuple) is a pointer to a 4-byte slot per field in field-name order, with its layout's number (see
/// `RecordTable`) in the 4 bytes before the pointer. A tag without a payload is its code (see `tagCode`), which
/// is odd; a tag with one is a pointer to an 8-byte cell of (code, payload). The heap is a bump allocator from
/// `$heap_ptr` that hands out 4-byte-aligned blocks, so a pointer is never odd, and grows the memory when it runs
/// out; nothing is ever freed.
let private runtimeHelpers : Map<string, string> =
    let lines (items: string list) = items |> List.map (fun line -> "  " + line) |> String.concat "\n"
    [ Helpers.alloc,
      lines
          [ $"(func ${Helpers.alloc} (param $size i32) (result i32)"
            "  (local $ptr i32)"
            "  global.get $heap_ptr"
            "  local.set $ptr"
            "  local.get $ptr"
            "  local.get $size"
            "  i32.const 3"
            "  i32.add"
            "  i32.const -4"
            "  i32.and"
            "  i32.add"
            "  global.set $heap_ptr"
            "  block $fits"
            "    loop $grow"
            "      global.get $heap_ptr"
            "      memory.size"
            "      i32.const 65536"
            "      i32.mul"
            "      i32.le_u"
            "      br_if $fits"
            "      i32.const 1"
            "      memory.grow"
            "      i32.const -1"
            "      i32.eq"
            "      if"
            "        unreachable"
            "      end"
            "      br $grow"
            "    end"
            "  end"
            "  local.get $ptr"
            ")" ]
      Helpers.copyBytes,
      lines
          [ $"(func ${Helpers.copyBytes} (param $dst i32) (param $src i32) (param $len i32)"
            "  (local $i i32)"
            "  block $done"
            "    loop $copy"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $done"
            "      local.get $dst"
            "      local.get $i"
            "      i32.add"
            "      local.get $src"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      i32.store8"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $copy"
            "    end"
            "  end"
            ")" ]
      Helpers.stringLength,
      lines
          [ $"(func ${Helpers.stringLength} (param $s i32) (result i32)"
            "  local.get $s"
            "  i32.const 4"
            "  i32.sub"
            "  i32.load"
            ")" ]
      Helpers.stringAlloc,
      lines
          [ $"(func ${Helpers.stringAlloc} (param $len i32) (result i32)"
            "  (local $base i32)"
            "  local.get $len"
            "  i32.const 5"
            "  i32.add"
            $"  call ${Helpers.alloc}"
            "  local.set $base"
            "  local.get $base"
            "  local.get $len"
            "  i32.store"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            "  local.get $len"
            "  i32.add"
            "  i32.const 0"
            "  i32.store8"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            ")" ]
      Helpers.stringByteAt,
      lines
          [ $"(func ${Helpers.stringByteAt} (param $s i32) (param $i i32) (result i32)"
            "  local.get $i"
            "  i32.const 0"
            "  i32.lt_s"
            "  local.get $i"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.ge_s"
            "  i32.or"
            "  if (result i32)"
            "    i32.const -1"
            "  else"
            "    local.get $s"
            "    local.get $i"
            "    i32.add"
            "    i32.load8_u"
            "  end"
            ")" ]
      Helpers.stringSlice,
      lines
          [ $"(func ${Helpers.stringSlice} (param $s i32) (param $start i32) (param $len i32) (result i32)"
            "  (local $total i32)"
            "  (local $out i32)"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  local.set $total"
            "  local.get $start"
            "  i32.const 0"
            "  i32.lt_s"
            "  if"
            "    i32.const 0"
            "    local.set $start"
            "  end"
            "  local.get $start"
            "  local.get $total"
            "  i32.gt_s"
            "  if"
            "    local.get $total"
            "    local.set $start"
            "  end"
            "  local.get $len"
            "  i32.const 0"
            "  i32.lt_s"
            "  if"
            "    i32.const 0"
            "    local.set $len"
            "  end"
            "  local.get $len"
            "  local.get $total"
            "  local.get $start"
            "  i32.sub"
            "  i32.gt_s"
            "  if"
            "    local.get $total"
            "    local.get $start"
            "    i32.sub"
            "    local.set $len"
            "  end"
            "  local.get $len"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $s"
            "  local.get $start"
            "  i32.add"
            "  local.get $len"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringFromByte,
      lines
          [ $"(func ${Helpers.stringFromByte} (param $byte i32) (result i32)"
            "  (local $out i32)"
            "  i32.const 1"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $byte"
            "  i32.store8"
            "  local.get $out"
            ")" ]
      Helpers.stringConcat,
      lines
          [ $"(func ${Helpers.stringConcat} (param $a i32) (param $b i32) (result i32)"
            "  (local $lenA i32)"
            "  (local $lenB i32)"
            "  (local $out i32)"
            "  local.get $a"
            $"  call ${Helpers.stringLength}"
            "  local.set $lenA"
            "  local.get $b"
            $"  call ${Helpers.stringLength}"
            "  local.set $lenB"
            "  local.get $lenA"
            "  local.get $lenB"
            "  i32.add"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $a"
            "  local.get $lenA"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            "  local.get $lenA"
            "  i32.add"
            "  local.get $b"
            "  local.get $lenB"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringEquals,
      lines
          [ $"(func ${Helpers.stringEquals} (param $a i32) (param $b i32) (result i32)"
            "  (local $len i32)"
            "  (local $i i32)"
            "  local.get $a"
            $"  call ${Helpers.stringLength}"
            "  local.set $len"
            "  local.get $len"
            "  local.get $b"
            $"  call ${Helpers.stringLength}"
            "  i32.ne"
            "  if"
            "    i32.const 0"
            "    return"
            "  end"
            "  block $done"
            "    loop $compare"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $done"
            "      local.get $a"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      local.get $b"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      i32.ne"
            "      if"
            "        i32.const 0"
            "        return"
            "      end"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $compare"
            "    end"
            "  end"
            "  i32.const 1"
            ")" ]
      // Writes a string to stdout (fd 1) with the iovec at 0 and nwritten at 8, like the dbg helpers.
      Helpers.writeStdout,
      lines
          [ $"(func ${Helpers.writeStdout} (param $s i32)"
            "  i32.const 0"
            "  local.get $s"
            "  i32.store"
            "  i32.const 4"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.store"
            "  i32.const 1"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_write"
            "  drop"
            ")" ]
      // The same as writing to stdout, on stderr (fd 2).
      Helpers.writeStderr,
      lines
          [ $"(func ${Helpers.writeStderr} (param $s i32)"
            "  i32.const 0"
            "  local.get $s"
            "  i32.store"
            "  i32.const 4"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.store"
            "  i32.const 2"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_write"
            "  drop"
            ")" ]
      // Reads one byte of stdin (fd 0) into offset 12 through the iovec at 0, with the count read at 8. Gives
      // back -1 when nothing was read (the end of the input) or the read failed. A call `f()` to an intrinsic
      // passes no argument for the unit, so it takes none.
      Helpers.readStdinByte,
      lines
          [ $"(func ${Helpers.readStdinByte} (result i32)"
            "  i32.const 0"
            "  i32.const 12"
            "  i32.store"
            "  i32.const 4"
            "  i32.const 1"
            "  i32.store"
            "  i32.const 0"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_read"
            "  if (result i32)"
            "    i32.const -1"
            "  else"
            "    i32.const 8"
            "    i32.load"
            "    if (result i32)"
            "      i32.const 12"
            "      i32.load8_u"
            "    else"
            "      i32.const -1"
            "    end"
            "  end"
            ")" ]
      // Stores one field of a freshly allocated record and hands the record pointer back, so a record
      // literal is built entirely on the operand stack: `base value offset -> base`. That keeps nested
      // literals safe, since an inner allocation can never disturb an outer one held on the stack.
      Helpers.recordSet,
      lines
          [ $"(func ${Helpers.recordSet} (param $base i32) (param $value i32) (param $offset i32) (result i32)"
            "  local.get $base"
            "  local.get $offset"
            "  i32.add"
            "  local.get $value"
            "  i32.store"
            "  local.get $base"
            ")" ]
      Helpers.recordCopy,
      lines
          [ $"(func ${Helpers.recordCopy} (param $src i32) (param $size i32) (result i32)"
            "  (local $dst i32)"
            "  local.get $size"
            $"  call ${Helpers.alloc}"
            "  local.set $dst"
            "  local.get $dst"
            "  local.get $src"
            "  local.get $size"
            $"  call ${Helpers.copyBytes}"
            "  local.get $dst"
            ")" ]
      // A tag with a payload: `code payload -> cell`.
      Helpers.tagBox,
      lines
          [ $"(func ${Helpers.tagBox} (param $code i32) (param $payload i32) (result i32)"
            "  (local $cell i32)"
            "  i32.const 8"
            $"  call ${Helpers.alloc}"
            "  local.tee $cell"
            "  local.get $code"
            "  i32.store"
            "  local.get $cell"
            "  local.get $payload"
            "  i32.store offset=4"
            "  local.get $cell"
            ")" ]
      // Which tag a tag value is: the value itself when it is a code (odd), else the code in its cell.
      Helpers.tagCode,
      lines
          [ $"(func ${Helpers.tagCode} (param $tag i32) (result i32)"
            "  local.get $tag"
            "  i32.const 1"
            "  i32.and"
            "  if (result i32)"
            "    local.get $tag"
            "  else"
            "    local.get $tag"
            "    i32.load"
            "  end"
            ")" ]
      // `==` on tags: the same code and, for tags with payloads, the same payload word.
      Helpers.tagEquals,
      lines
          [ $"(func ${Helpers.tagEquals} (param $a i32) (param $b i32) (result i32)"
            "  local.get $a"
            "  local.get $b"
            "  i32.eq"
            "  if"
            "    i32.const 1"
            "    return"
            "  end"
            "  local.get $a"
            "  local.get $b"
            "  i32.or"
            "  i32.const 1"
            "  i32.and"
            "  if"
            "    i32.const 0"
            "    return"
            "  end"
            "  local.get $a"
            "  i32.load"
            "  local.get $b"
            "  i32.load"
            "  i32.eq"
            "  local.get $a"
            "  i32.load offset=4"
            "  local.get $b"
            "  i32.load offset=4"
            "  i32.eq"
            "  i32.and"
            ")" ]
      // A float is boxed: an 8-byte block holding the f64, passed around by pointer like every other
      // value, so locals, record slots and calls stay 32-bit.
      Helpers.floatBox,
      lines
          [ $"(func ${Helpers.floatBox} (param $value f64) (result i32)"
            "  (local $box i32)"
            "  i32.const 8"
            $"  call ${Helpers.alloc}"
            "  local.set $box"
            "  local.get $box"
            "  local.get $value"
            "  f64.store"
            "  local.get $box"
            ")" ]
      // Formats a boxed float as `-?int.frac` with the fraction rounded to six digits and trailing zeros
      // dropped (keeping one): 6.28, 4.0, -1.5, 0.333333.
      Helpers.floatToString,
      lines
          [ $"(func ${Helpers.floatToString} (param $box i32) (result i32)"
            "  (local $x f64)"
            "  (local $base i32)"
            "  (local $cursor i32)"
            "  (local $int i64)"
            "  (local $frac i64)"
            "  (local $tmp i64)"
            "  (local $count i32)"
            "  (local $pos i32)"
            "  (local $scale i64)"
            "  local.get $box"
            "  f64.load"
            "  local.set $x"
            "  ;; 4 length bytes, a sign, up to 20 integer digits, the point, 6 digits and a terminator"
            "  i32.const 36"
            $"  call ${Helpers.alloc}"
            "  local.set $base"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            "  local.set $cursor"
            "  local.get $x"
            "  f64.const 0"
            "  f64.lt"
            "  if"
            "    local.get $cursor"
            "    i32.const 45"
            "    i32.store8"
            "    local.get $cursor"
            "    i32.const 1"
            "    i32.add"
            "    local.set $cursor"
            "    local.get $x"
            "    f64.neg"
            "    local.set $x"
            "  end"
            "  local.get $x"
            "  i64.trunc_sat_f64_u"
            "  local.set $int"
            "  local.get $x"
            "  local.get $int"
            "  f64.convert_i64_u"
            "  f64.sub"
            "  f64.const 1000000"
            "  f64.mul"
            "  f64.const 0.5"
            "  f64.add"
            "  i64.trunc_sat_f64_u"
            "  local.set $frac"
            "  local.get $frac"
            "  i64.const 1000000"
            "  i64.ge_u"
            "  if"
            "    local.get $int"
            "    i64.const 1"
            "    i64.add"
            "    local.set $int"
            "    i64.const 0"
            "    local.set $frac"
            "  end"
            "  ;; integer digits: count them, then write them back to front"
            "  i32.const 0"
            "  local.set $count"
            "  local.get $int"
            "  local.set $tmp"
            "  loop $count_digits"
            "    local.get $count"
            "    i32.const 1"
            "    i32.add"
            "    local.set $count"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $tmp"
            "    local.get $tmp"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $count_digits"
            "  end"
            "  local.get $cursor"
            "  local.get $count"
            "  i32.add"
            "  i32.const 1"
            "  i32.sub"
            "  local.set $pos"
            "  local.get $int"
            "  local.set $tmp"
            "  loop $write_digits"
            "    local.get $pos"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.rem_u"
            "    i32.wrap_i64"
            "    i32.const 48"
            "    i32.add"
            "    i32.store8"
            "    local.get $pos"
            "    i32.const 1"
            "    i32.sub"
            "    local.set $pos"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $tmp"
            "    local.get $tmp"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $write_digits"
            "  end"
            "  local.get $cursor"
            "  local.get $count"
            "  i32.add"
            "  local.set $cursor"
            "  local.get $cursor"
            "  i32.const 46"
            "  i32.store8"
            "  local.get $cursor"
            "  i32.const 1"
            "  i32.add"
            "  local.set $cursor"
            "  ;; six fraction digits, most significant first"
            "  i64.const 100000"
            "  local.set $scale"
            "  loop $write_frac"
            "    local.get $cursor"
            "    local.get $frac"
            "    local.get $scale"
            "    i64.div_u"
            "    i64.const 10"
            "    i64.rem_u"
            "    i32.wrap_i64"
            "    i32.const 48"
            "    i32.add"
            "    i32.store8"
            "    local.get $cursor"
            "    i32.const 1"
            "    i32.add"
            "    local.set $cursor"
            "    local.get $scale"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $scale"
            "    local.get $scale"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $write_frac"
            "  end"
            "  ;; drop trailing zeros, keeping one digit after the point"
            "  block $trim_done"
            "    loop $trim"
            "      local.get $cursor"
            "      i32.const 1"
            "      i32.sub"
            "      i32.load8_u"
            "      i32.const 48"
            "      i32.ne"
            "      br_if $trim_done"
            "      local.get $cursor"
            "      i32.const 2"
            "      i32.sub"
            "      i32.load8_u"
            "      i32.const 46"
            "      i32.eq"
            "      br_if $trim_done"
            "      local.get $cursor"
            "      i32.const 1"
            "      i32.sub"
            "      local.set $cursor"
            "      br $trim"
            "    end"
            "  end"
            "  local.get $base"
            "  local.get $cursor"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            "  i32.sub"
            "  i32.store"
            "  local.get $cursor"
            "  i32.const 0"
            "  i32.store8"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            ")" ]
      Helpers.listCons,
      lines
          [ $"(func ${Helpers.listCons} (param $head i32) (param $tail i32) (result i32)"
            "  (local $cell i32)"
            "  i32.const 8"
            $"  call ${Helpers.alloc}"
            "  local.set $cell"
            "  local.get $cell"
            "  local.get $head"
            "  i32.store"
            "  local.get $cell"
            "  i32.const 4"
            "  i32.add"
            "  local.get $tail"
            "  i32.store"
            "  local.get $cell"
            ")" ]
      Helpers.listLength,
      lines
          [ $"(func ${Helpers.listLength} (param $list i32) (result i32)"
            "  (local $count i32)"
            "  block $done"
            "    loop $walk"
            "      local.get $list"
            "      i32.eqz"
            "      br_if $done"
            "      local.get $count"
            "      i32.const 1"
            "      i32.add"
            "      local.set $count"
            "      local.get $list"
            "      i32.const 4"
            "      i32.add"
            "      i32.load"
            "      local.set $list"
            "      br $walk"
            "    end"
            "  end"
            "  local.get $count"
            ")" ]
      // a ++ b copies the cells of a in front of b (b itself is shared, lists being immutable).
      Helpers.listConcat,
      lines
          [ $"(func ${Helpers.listConcat} (param $a i32) (param $b i32) (result i32)"
            "  local.get $a"
            "  i32.eqz"
            "  if (result i32)"
            "    local.get $b"
            "  else"
            "    local.get $a"
            "    i32.load"
            "    local.get $a"
            "    i32.const 4"
            "    i32.add"
            "    i32.load"
            "    local.get $b"
            $"    call ${Helpers.listConcat}"
            $"    call ${Helpers.listCons}"
            "  end"
            ")" ] ]
    |> Map.ofList

let private ctxFnName (name: string, _, _) = name
let private ctxFnSlot (_, slot: int, _) = slot
let private ctxFnArity (_, _, arity: int) = arity

// Convert a TypeExpr to a string key (e.g., TypeApply("ToString", [TypeVar "a"]) -> "ToString_a")
let rec private typeExprToKey (t: TypeExpr) =
    match t.Kind with
    | TypeName name -> name
    | TypeVar name -> name
    | TypeApply(name, typeArgs) -> 
        let argsStr = typeArgs |> List.map typeExprToKey |> String.concat "_"
        $"{name}_{argsStr}"
    | _ -> "unknown"

/// The key a context member is known by in the backend: the member's name for a plain context, and
/// `<context key>_<member>` for an instance of a parameterised one (`ToString_int_toString`).
let private contextMemberKey (contextType: TypeExpr option) (memberName: string) =
    match contextType with
    | Some ({ TypeExpr.Kind = TypeApply _ } as applied) -> $"{typeExprToKey applied}_{memberName}"
    | _ -> memberName

// Extract context type expressions from a TypeWithContext or TypeContext annotation
let private extractContextTypes (typeExpr: TypeExpr option) : TypeExpr list =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, _))) -> contexts
    | Some (Kind (TypeContext contexts)) -> contexts
    | _ -> []

// Extract context type names from a TypeWithContext or TypeContext annotation
let private extractContextTypeNames (typeExpr: TypeExpr option) : string list =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, _))) ->
        contexts |> List.choose (function
            | Kind (TypeName name) -> Some name
            | Kind (TypeApply(name, _)) -> Some name
            | _ -> None)
    | Some (Kind (TypeContext contexts)) ->
        contexts |> List.choose (function
            | Kind (TypeName name) -> Some name
            | Kind (TypeApply(name, _)) -> Some name
            | _ -> None)
    | _ -> []

// Get the inner type (without context) from a TypeWithContext or TypeContext
let private unwrapContextType (typeExpr: TypeExpr option) : TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(_, inner))) -> Some inner
    | Some (Kind (TypeContext _)) -> None  // TypeContext alone has no inner type
    | other -> other

let private sanitizeIdentifier (name: string) =
    let sanitized =
        name
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '_' then c else '_')
        |> Seq.toArray
        |> String
    if String.IsNullOrWhiteSpace sanitized then "_"
    elif Char.IsDigit sanitized.[0] then "_" + sanitized
    else sanitized

/// The f64 instruction for an operator on floats; comparisons leave an i32, arithmetic an f64 to box.
let private wasmFloatInstruction op =
    match op with
    | "+" -> Some "f64.add"
    | "-" -> Some "f64.sub"
    | "*" -> Some "f64.mul"
    | "/" -> Some "f64.div"
    | "==" -> Some "f64.eq"
    | "!=" -> Some "f64.ne"
    | "<" -> Some "f64.lt"
    | "<=" -> Some "f64.le"
    | ">" -> Some "f64.gt"
    | ">=" -> Some "f64.ge"
    | _ -> None

let private wasmFloatConst (value: float) =
    if System.Double.IsNaN value then "nan"
    elif System.Double.IsPositiveInfinity value then "inf"
    elif System.Double.IsNegativeInfinity value then "-inf"
    else value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)

let private wasmBinaryInstruction op =
    match op with
    | "+" -> Some "i32.add"
    | "-" -> Some "i32.sub"
    | "*" -> Some "i32.mul"
    | "/" -> Some "i32.div_s"
    | "%" -> Some "i32.rem_s"
    | "==" -> Some "i32.eq"
    | "!=" -> Some "i32.ne"
    | "<" -> Some "i32.lt_s"
    | "<=" -> Some "i32.le_s"
    | ">" -> Some "i32.gt_s"
    | ">=" -> Some "i32.ge_s"
    | "and" -> Some "i32.and"
    | "or" -> Some "i32.or"
    | _ -> None

/// Whether `name` is a nominal type's, qualified by its module block or not (`$Money`, `main.money.$Money`).
let private isNominalTypeName (name: string) =
    let own = match name.LastIndexOf '.' with -1 -> name | dot -> name.Substring(dot + 1)
    own.StartsWith "$"

let private flattenCallArgs (args: Expression list) =
    match args with
    | [Kind (TupleExpr items)] -> items
    | _ -> args

/// The function-table slot of a top-level function used as a value, assigning one on first use.
let private functionValueSlot (env: TranspileEnv) (name: string) (arity: int) : int =
    match env.FunctionTable.Values.TryGetValue name with
    | true, ctxFn -> ctxFnSlot ctxFn
    | _ ->
        let slot = env.FunctionTable.NextSlot
        env.FunctionTable.NextSlot <- slot + 1
        env.FunctionTable.Values.[name] <- (sanitizeIdentifier name, slot, arity)
        slot

let rec private collectExprLocalNames (expr: Expression) : string list =
    match expr.Kind with
    | Block statements -> collectStatementLocalNames statements
    | BinaryOp(_, left, right) -> (collectExprLocalNames left) @ (collectExprLocalNames right)
    | IfExpr(cond, thenExpr, elseExpr) ->
        (collectExprLocalNames cond) @ (collectExprLocalNames thenExpr) @ (collectExprLocalNames elseExpr)
    | FunctionCall(_, _, args) -> args |> List.collect collectExprLocalNames
    | Lambda(_, body) -> collectExprLocalNames body
    | Pipe(value, _, _, args) -> (collectExprLocalNames value) @ (args |> List.collect collectExprLocalNames)
    | TupleExpr items
    | ListExpr items -> items |> List.collect collectExprLocalNames
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectExprLocalNames value
            | PositionalField value -> collectExprLocalNames value)
    | Match(values, arms) ->
        let valueLocals = values |> List.collect collectExprLocalNames
        let armLocals =
            arms
            |> List.collect (fun (_, armExpr) -> collectExprLocalNames armExpr)
        valueLocals @ armLocals
    | UseIn(_, body) -> collectExprLocalNames body
    | MemberAccess(inner, _, _) -> collectExprLocalNames inner
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectExprLocalNames
    | TagExpr(_, payload) -> payload |> Option.map collectExprLocalNames |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr inner -> collectExprLocalNames inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectExprLocalNames value
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectStatementLocalNames (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, name, _, rhs) -> name :: collectExprLocalNames rhs
        | ExprStatement expr -> collectExprLocalNames expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let private collectBlockLocalNames (statements: Statement list) =
    collectStatementLocalNames statements |> List.distinct

/// Every identifier a pattern binds, however deeply nested.
let rec private nestedPatternIdentifiers (pattern: Pattern) : string list =
    match pattern.Kind with
    | IdentifierPattern ident -> [ ident ]
    | TagPattern(_, Some payload) -> nestedPatternIdentifiers payload
    | ListPattern(elements, restOpt) ->
        (elements |> List.collect nestedPatternIdentifiers)
        @ (restOpt |> Option.map nestedPatternIdentifiers |> Option.defaultValue [])
    | TuplePattern patterns
    | RecordPattern(_, patterns) -> patterns |> List.collect nestedPatternIdentifiers
    | _ -> []

/// The identifiers a pattern binds that live in `__payload_<name>` locals: tag payloads, list elements and
/// rests, and tuple elements. (A plain identifier pattern at the top of an arm aliases the scrutinee local
/// and needs no local of its own.)
let private patternBoundIdentifiers (pattern: Pattern) : string list =
    match pattern.Kind with
    | IdentifierPattern _ -> []
    | _ -> nestedPatternIdentifiers pattern

let rec private collectPayloadLocalsExpr (expr: Expression) =
    match expr.Kind with
    | Match(_, arms) ->
        arms |> List.collect (fun (patterns, armExpr) ->
            let payloads = patterns |> List.collect patternBoundIdentifiers
            List.append payloads (collectPayloadLocalsExpr armExpr))
    | FunctionCall(_, _, args) -> args |> List.collect collectPayloadLocalsExpr
    | BinaryOp(_, left, right) -> List.append (collectPayloadLocalsExpr left) (collectPayloadLocalsExpr right)
    | IfExpr(cond, thenExpr, elseExpr) ->
        List.append (collectPayloadLocalsExpr cond) (List.append (collectPayloadLocalsExpr thenExpr) (collectPayloadLocalsExpr elseExpr))
    | Lambda(_, body) -> collectPayloadLocalsExpr body
    | Pipe(value, _, _, args) -> List.append (collectPayloadLocalsExpr value) (args |> List.collect collectPayloadLocalsExpr)
    | Block statements ->
        statements |> List.map _.Kind |> List.collect (function
            | DefStatement(_, _, _, rhs) -> collectPayloadLocalsExpr rhs
            | ExprStatement value -> collectPayloadLocalsExpr value
            | _ -> [])
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectPayloadLocalsExpr
    | RecordExpr fields ->
        fields |> List.collect (function
            | NamedField(_, value) -> collectPayloadLocalsExpr value
            | PositionalField value -> collectPayloadLocalsExpr value)
    | UseIn(_, body) -> collectPayloadLocalsExpr body
    | MemberAccess(value, _, _) -> collectPayloadLocalsExpr value
    | TagExpr(_, payload) -> payload |> Option.map collectPayloadLocalsExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        parts |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectPayloadLocalsExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectPayloadLocalsExpr value
    | ContextMemberAccess(_, _)
    | ContextMemberCall(_, _, _)
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

let private createNameMap (names: string list) =
    names
    |> List.fold (fun (acc, used) name ->
        let baseName = sanitizeIdentifier name
        let rec findAvailable candidate index =
            if used |> Set.contains candidate then
                findAvailable ($"{baseName}_{index}") (index + 1)
            else
                candidate
        let chosen = findAvailable baseName 1
        (acc |> Map.add name chosen, used |> Set.add chosen)) (Map.empty, Set.empty)
    |> fst

let private sanitizeImportSuffix (name: string) = sanitizeIdentifier name

/// A tag's code: odd, so a tag without a payload (which is its code) can't be mistaken for a pointer to the cell
/// of a tag with one.
let private tagCode (id: int) = (id <<< 1) ||| 1

let private stringDataBaseOffset = 256

let private heapBaseOffset = 4096  // Start heap after string data region

let private dbgDataBaseOffset = 128

// dbg writes an integer's sign and digits directly after the "dbg: " prefix (at 133), so that one
// fd_write from the prefix to the end of the digits prints the whole line.
let private dbgDigitBufferOffset = dbgDataBaseOffset + 5
let private dbgStrNewlineOffset = dbgDataBaseOffset + 96  // newline byte at 224 (clear of dbg's buffer)

let private escapeWasmData (bytes: byte array) =
    let sb = StringBuilder()
    for b in bytes do
        let c = char b
        if b >= 0x20uy && b <= 0x7Euy && c <> '"' && c <> '\\' then
            sb.Append c |> ignore
        else
            sb.Append(sprintf "\\%02x" b) |> ignore
    sb.ToString()

let private buildStringLiteralMap (literals: string list) =
    let mutable offset = stringDataBaseOffset
    let entries =
        literals
        |> List.map (fun value ->
            let bytes = Encoding.UTF8.GetBytes value
            let lengthBytes = BitConverter.GetBytes bytes.Length
            let dataBytes = Array.concat [ lengthBytes; bytes; [| 0uy |] ]
            let currentOffset = offset
            offset <- offset + dataBytes.Length
            value, currentOffset, dataBytes)
    let map = entries |> List.map (fun (value, baseOffset, _) -> value, baseOffset + 4) |> Map.ofList
    map, entries

let private trySimpleTypeName (ty: Ty) =
    match Unifier.unalias ty with
    | TyPrimitive "string" -> Some "string"
    | TyPrimitive "int" -> Some "int"
    | TyPrimitive "bool" -> Some "bool"
    | TyPrimitive "float" -> Some "float"
    | TyApply("list", _) -> Some "list"
    | _ -> None

/// The payload type of `#tagName(...)` within `ty`, which may be the tag itself, a union containing it, or a
/// nominal type or alias wrapping either.
let rec private tryTagPayloadType (tagName: string) (ty: Ty) : Ty option =
    match ty with
    | TyTag(name, Some payloadTy) when name = tagName -> Some payloadTy
    | TyUnion options -> options |> List.tryPick (tryTagPayloadType tagName)
    | TyNominal(_, underlying, _)
    | TyAlias(_, _, underlying) -> tryTagPayloadType tagName underlying
    | _ -> None

/// The tags a value of type `ty` can be, each with its payload type, when `ty` is a tag or a union of tags. A
/// union's rest variable names no tag of its own, so it adds none.
let rec private tryTagMembers (ty: Ty) : (string * Ty option) list option =
    match ty with
    | TyTag(name, payloadTy) -> Some [ name, payloadTy ]
    | TyUnion options ->
        match options |> List.choose (function TyTag(name, payloadTy) -> Some (name, payloadTy) | _ -> None) with
        | [] -> None
        | members -> Some members
    | TyNominal(_, underlying, _)
    | TyAlias(_, _, underlying) -> tryTagMembers underlying
    | _ -> None

let rec private tryListElementType (ty: Ty) : Ty option =
    match ty with
    | TyApply("list", [ elementTy ]) -> Some elementTy
    | TyNominal(_, underlying, _)
    | TyAlias(_, _, underlying) -> tryListElementType underlying
    | _ -> None

let rec private tryRecordFieldTys (ty: Ty) : Map<string, Ty> option =
    match ty with
    | TyRecord fields -> Some fields
    | TyNominal(_, underlying, _)
    | TyAlias(_, _, underlying) -> tryRecordFieldTys underlying
    | _ -> None

/// The types of a lambda's parameters, from the function type `ty` inference gave it.
let private lambdaParameterTys (parameters: (Identifier * TypeExpr option) list) (ty: Ty) : (Identifier * Ty) list =
    match parameters, Unifier.unalias ty with
    | [ (name, _) ], TyFunc(inputTy, _) -> [ name, inputTy ]
    | _, TyFunc(inputTy, _) ->
        match Unifier.unalias inputTy with
        | TyRecord fields ->
            parameters
            |> List.mapi (fun index (name, _) -> fields |> Map.tryFind (string index) |> Option.map (fun ty -> name, ty))
            |> List.choose id
        | _ -> []
    | _ -> []

/// The inferred types of the identifiers `pattern` binds when matched against a value of type `ty`:
/// `#some(n)` binds `n` at the tag's payload type, `[x, ...rest]` binds `x` at the element type and `rest`
/// at the list's type, and a bare `n` binds at `ty` itself.
let rec private patternBoundTys (pattern: Pattern) (ty: Ty) : (string * Ty) list =
    let under (tyOpt: Ty option) (inner: Pattern) =
        tyOpt |> Option.map (patternBoundTys inner) |> Option.defaultValue []
    match pattern.Kind with
    | IdentifierPattern ident -> [ ident, ty ]
    | TagPattern(tagName, Some payload) -> under (tryTagPayloadType tagName ty) payload
    | ListPattern(elements, restOpt) ->
        let elementTy = tryListElementType ty
        let elementBindings = elements |> List.collect (under elementTy)
        let restBinding =
            match restOpt with
            | Some rest -> patternBoundTys rest ty
            | None -> []
        elementBindings @ restBinding
    | TuplePattern patterns ->
        let fieldTys = tryRecordFieldTys ty
        patterns
        |> List.mapi (fun index element -> under (fieldTys |> Option.bind (Map.tryFind (string index))) element)
        |> List.concat
    // `$Email(raw)` binds `raw` at the nominal type's shape.
    | RecordPattern(typeName, [ payload ]) when isNominalTypeName typeName ->
        match Unifier.unalias ty with
        | TyNominal(_, underlying, _) -> patternBoundTys payload underlying
        | _ -> patternBoundTys payload ty
    | _ -> []

/// The simple types of the identifiers a typed pattern binds (see `patternBoundTys`).
let private patternBoundValueTypes (typedPattern: TypedPattern) : (string * string) list =
    patternBoundTys typedPattern.Pattern typedPattern.Type
    |> List.choose (fun (ident, ty) -> trySimpleTypeName ty |> Option.map (fun tyName -> ident, tyName))

/// The typed expression for `target` (an expression as written, ranges and all) within `typedExpr`.
let rec private tryFindTypedExpr (target: Expression) (typedExpr: TypedExpr) : TypedExpr option =
    if typedExpr.Expr = target then
        Some typedExpr
    else
        let inStatement statement =
            match statement with
            | TypedDefStatement(_, _, _, expr)
            | TypedExprStatement expr
            | TypedUseStatement(_, Some expr) -> tryFindTypedExpr target expr
            | TypedUseStatement(_, None)
            | TypedImportStatement _
            | TypedTypeDefStatement _ -> None
        typedExpr.Body
        |> Option.bind (tryFindTypedExpr target)
        |> Option.orElseWith (fun () -> typedExpr.Statements |> Option.bind (List.tryPick inStatement))
        |> Option.orElseWith (fun () -> typedExpr.MatchArms |> Option.bind (List.tryPick (snd >> tryFindTypedExpr target)))
        |> Option.orElseWith (fun () -> typedExpr.Arguments |> Option.bind (List.tryPick (tryFindTypedExpr target)))

/// The full inferred types of a function's parameters and the locals defined anywhere in its body.
let private collectTypedValueTys (typedExpr: TypedExpr) : Map<string, Ty> =
    let parameterTys =
        match typedExpr.Expr.Kind with
        | Lambda(parameters, _) -> lambdaParameterTys parameters typedExpr.Type
        | _ -> []
    let rec collectExpr (acc: Map<string, Ty>) (expr: TypedExpr) =
        let accWithBody =
            match expr.Body with
            | Some body -> collectExpr acc body
            | None -> acc
        let accWithStatements =
            match expr.Statements with
            | Some statements -> statements |> List.fold collectStatement accWithBody
            | None -> accWithBody
        match expr.MatchArms with
        | Some arms -> arms |> List.fold (fun state (_, armExpr) -> collectExpr state armExpr) accWithStatements
        | None -> accWithStatements
    and collectStatement (acc: Map<string, Ty>) statement =
        match statement with
        | TypedDefStatement(_, name, _, expr) -> collectExpr (acc |> Map.add name expr.Type) expr
        | TypedExprStatement expr -> collectExpr acc expr
        | TypedUseStatement(_, Some expr) -> collectExpr acc expr
        | TypedUseStatement(_, None)
        | TypedImportStatement _
        | TypedTypeDefStatement _ -> acc
    collectExpr (Map.ofList parameterTys) typedExpr

/// The simple types inference found for a function's parameters and the locals of its body.
let private collectTypedValueTypes (typedExpr: TypedExpr) =
    let parameterTypes =
        match typedExpr.Expr.Kind with
        | Lambda(parameters, _) ->
            lambdaParameterTys parameters typedExpr.Type
            |> List.choose (fun (name, ty) -> trySimpleTypeName ty |> Option.map (fun tyName -> name, tyName))
        | _ -> []
    let rec collectExpr (acc: Map<string, string>) (expr: TypedExpr) =
        let accWithBody =
            match expr.Body with
            | Some body -> collectExpr acc body
            | None -> acc
        let accWithStatements =
            match expr.Statements with
            | Some statements -> statements |> List.fold collectStatement accWithBody
            | None -> accWithBody
        match expr.MatchArms with
        | Some arms ->
            arms
            |> List.fold (fun state (typedPatterns, armExpr) ->
                let withBindings =
                    typedPatterns
                    |> List.collect patternBoundValueTypes
                    |> List.fold (fun acc (ident, tyName) -> acc |> Map.add ident tyName) state
                collectExpr withBindings armExpr) accWithStatements
        | None -> accWithStatements
    and collectStatement (acc: Map<string, string>) statement =
        match statement with
        | TypedDefStatement(_, name, _, expr) ->
            let updated =
                match trySimpleTypeName expr.Type with
                | Some tyName -> acc |> Map.add name tyName
                | None -> acc
            collectExpr updated expr
        | TypedExprStatement expr -> collectExpr acc expr
        | TypedUseStatement(_, exprOpt) ->
            match exprOpt with
            | Some expr -> collectExpr acc expr
            | None -> acc
        | TypedImportStatement _
        | TypedTypeDefStatement _ -> acc
    collectExpr (Map.ofList parameterTypes) typedExpr

/// The fields of a record type, looking through nominal wrappers.
/// The inferred type of an expression, when `env.ValueTys` can tell: a named value, a field of a record-typed
/// expression, or the result of calling a known function.
let rec private tryExprTy (env: TranspileEnv) (expr: Expression) : Ty option =
    match expr.Kind with
    | IdentifierExpr name -> env.ValueTys |> Map.tryFind name
    | MemberAccess(inner, fieldName, _) ->
        let fieldTys ty =
            match Unifier.unalias ty with
            | TyOpenRecord(fields, _) -> Some fields
            | _ -> tryRecordFieldTys ty
        tryExprTy env inner |> Option.bind fieldTys |> Option.bind (Map.tryFind fieldName)
    | FunctionCall("__with", _, baseExpr :: _) -> tryExprTy env baseExpr  // a record update has the record's type
    | FunctionCall("dbg", _, [ value ]) -> tryExprTy env value  // dbg returns its argument
    | FunctionCall(name, _, _)
    | Pipe(_, name, _, _) ->
        env.ValueTys
        |> Map.tryFind name
        |> Option.bind (fun ty ->
            match Unifier.unalias ty with
            | TyFunc(_, returnTy) -> Some returnTy
            | _ -> None)
    | _ -> None

/// The layout (field names in storage order) of a record-typed expression, from its inferred type. Records are
/// stored with their fields sorted by name, which is also the order of a `Map`'s keys.
let private tryRecordLayoutOfExpr (env: TranspileEnv) (expr: Expression) : string list option =
    tryExprTy env expr
    |> Option.bind tryRecordFieldTys
    |> Option.map (fun fields -> fields |> Map.toList |> List.map fst)

/// The number of the record layout with `fieldNames` (in storage order), numbering it if it is new.
let private recordLayoutId (env: TranspileEnv) (fieldNames: string list) =
    let layouts = env.RecordTable.LayoutIds
    match layouts.TryGetValue fieldNames with
    | true, id -> id
    | _ ->
        let id = layouts.Count + 1
        layouts.[fieldNames] <- id
        id

/// The number of `fieldName` as a field read from open records, numbering it if it is new.
let private openFieldId (env: TranspileEnv) (fieldName: string) =
    let fields = env.RecordTable.OpenFieldIds
    match fields.TryGetValue fieldName with
    | true, id -> id
    | _ ->
        let id = fields.Count + 1
        fields.[fieldName] <- id
        id

/// Whether `expr` is an open record: one known only to have certain fields, as a parameter is that a function
/// reads fields of, so where a field is depends on the record it is given.
let private isOpenRecordExpr (env: TranspileEnv) (expr: Expression) =
    match tryExprTy env expr |> Option.map Unifier.unalias with
    | Some (TyOpenRecord _) -> true
    | _ -> false

/// `$__nanyx_open_field(record, field)`: the value of a field of an open record, found from the layout number the
/// record carries. It knows every layout the module allocates that has a field read from an open record.
let private renderOpenFieldHelper (table: RecordTable) =
    let cases =
        [ for KeyValue(fieldNames, layoutId) in table.LayoutIds do
            for index, fieldName in List.indexed fieldNames do
                match table.OpenFieldIds.TryGetValue fieldName with
                | true, fieldId ->
                    yield
                        [ "    local.get $layout"
                          $"    i32.const {layoutId}"
                          "    i32.eq"
                          "    local.get $field"
                          $"    i32.const {fieldId}"
                          "    i32.eq"
                          "    i32.and"
                          "    if"
                          "      local.get $record"
                          $"      i32.const {index * 4}"
                          "      i32.add"
                          "      i32.load"
                          "      return"
                          "    end" ]
                | _ -> () ]
    [ $"  (func ${Helpers.openField} (param $record i32) (param $field i32) (result i32)"
      "    (local $layout i32)"
      "    local.get $record"
      "    i32.const 4"
      "    i32.sub"
      "    i32.load"
      "    local.set $layout"
      yield! List.concat cases
      "    unreachable"
      "  )" ]
    |> String.concat "\n"

/// The simple type ("string", "int" or "bool") of an expression, when the backend can tell. Strings matter most:
/// `+` and `==` are lowered differently when an operand is known to be a string.
let rec private tryGetExprTypeName (env: TranspileEnv) (expr: Expression) =
    let fromInferredType () = tryExprTy env expr |> Option.bind trySimpleTypeName
    match expr.Kind with
    | LiteralExpr(StringLit _) -> Some "string"
    | LiteralExpr(IntLit _) -> Some "int"
    | LiteralExpr(FloatLit _) -> Some "float"
    | LiteralExpr(BoolLit _) -> Some "bool"
    | InterpolatedString _ -> Some "string"
    | IdentifierExpr name ->
        env.ValueTypes |> Map.tryFind name
        |> Option.orElseWith (fun () -> env.ParamTypes |> Map.tryFind name)
        |> Option.orElseWith fromInferredType
    | MemberAccess(Kind (IdentifierExpr varName), fieldName, _) when env.RecordFieldTypes |> Map.containsKey fieldName |> not && env.RecordFieldTypes |> Map.containsKey varName ->
        env.RecordFieldTypes.[varName] |> Map.tryFind fieldName |> Option.orElseWith fromInferredType
    | MemberAccess _ -> fromInferredType ()
    | BinaryOp(op, _, _) when [ "=="; "!="; "<"; "<="; ">"; ">="; "and"; "or" ] |> List.contains op -> Some "bool"
    | FunctionCall("not", _, _) -> Some "bool"
    | FunctionCall("__with", _, baseExpr :: _) -> tryGetExprTypeName env baseExpr
    | BinaryOp(_, left, right) ->
        tryGetExprTypeName env left |> Option.orElseWith (fun () -> tryGetExprTypeName env right)
    | ListExpr _ -> Some "list"
    | FunctionCall(name, _, _)
    | Pipe(_, name, _, _) ->
        // A top-level function's declared result, or else what inference knows (a local closure's, say).
        env.FunctionReturnTypes |> Map.tryFind name |> Option.orElseWith fromInferredType
    | IfExpr(_, thenExpr, elseExpr) ->
        tryGetExprTypeName env thenExpr |> Option.orElseWith (fun () -> tryGetExprTypeName env elseExpr)
    | Block statements ->
        match List.tryLast statements with
        | Some (Kind (ExprStatement last)) -> tryGetExprTypeName env last
        | _ -> None
    | _ -> fromInferredType ()

let private isStringExpr (env: TranspileEnv) (expr: Expression) =
    tryGetExprTypeName env expr = Some "string"

let private collectInterpolatedStringExpr (expr: Expression) : bool =
    match expr.Kind with
    | InterpolatedString _ -> true
    | _ -> false

/// The immediate sub-expressions of `expr`, for traversals that only need to look inside.
let private exprChildren (expr: Expression) : Expression list =
    match expr.Kind with
    | InterpolatedString parts ->
        parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None)
    | Block statements ->
        statements |> List.map _.Kind
        |> List.choose (function
            | DefStatement(_, _, _, rhs) -> Some rhs
            | ExprStatement value -> Some value
            | _ -> None)
    | BinaryOp(_, left, right) -> [ left; right ]
    | IfExpr(cond, thenExpr, elseExpr) -> [ cond; thenExpr; elseExpr ]
    | FunctionCall(_, _, args) -> args
    | Lambda(_, body) -> [ body ]
    | Pipe(value, _, _, args) -> value :: args
    | TupleExpr items
    | ListExpr items -> items
    | RecordExpr fields ->
        fields
        |> List.map (function
            | NamedField(_, value) -> value
            | PositionalField value -> value)
    | Match(values, arms) -> values @ (arms |> List.map snd)
    | UseIn(_, body) -> [ body ]
    | MemberAccess(inner, _, _) -> [ inner ]
    | TagExpr(_, payload) -> Option.toList payload
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> [ value ]
    | ContextMemberCall(_, _, args) -> args
    | ContextMemberAccess(_, _)
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// Whether lowering `expr` needs the interpolation scratch locals (see `interpolationLocalNames`).
/// Used for function bodies and for the script start function alike, so both declare the same locals.
/// `dbg` of a value that may be a tag prints it as `"{value}"` does (see the `dbg` case of
/// `transpileExpression`). Its type is not known here, so any `dbg` of a non-literal counts.
let private isDbgOfValue (expr: Expression) =
    match expr.Kind with
    | FunctionCall("dbg", _, args) ->
        match flattenCallArgs args with
        | [Kind (LiteralExpr _ | TagExpr _ | InterpolatedString _)] -> false
        | [_] -> true
        | _ -> false
    | _ -> false

let rec private containsInterpolation (expr: Expression) : bool =
    match expr.Kind with
    | InterpolatedString _ -> true
    | _ when isDbgOfValue expr -> true
    | _ -> exprChildren expr |> List.exists containsInterpolation

/// The most `{...}` holes any single interpolated string in `expr` has: each hole is evaluated once
/// into its own `__interp_hole_N` local, so the function has to declare that many.
let rec private maxInterpolationHoles (expr: Expression) : int =
    let own =
        match expr.Kind with
        | InterpolatedString parts -> parts |> List.filter (function StringExpr _ -> true | _ -> false) |> List.length
        | _ when isDbgOfValue expr -> 1
        | _ -> 0
    exprChildren expr |> List.fold (fun acc child -> max acc (maxInterpolationHoles child)) own

let private interpolationHoleLocalName (index: int) = $"__interp_hole_{index}"

/// The locals of the `for` loops in an expression. A loop at nesting depth `d` keeps its position in
/// `__for_cursor_d` and `__for_end_d` and its element in `__for_<parameter>_d`; sibling loops share them,
/// nested loops differ by depth.
let private forLoopCursorLocal depth = $"__for_cursor_{depth}"
let private forLoopEndLocal depth = $"__for_end_{depth}"
let private forLoopElementLocal (parameter: string) depth = $"__for_{sanitizeIdentifier parameter}_{depth}"

let rec private collectForLoopLocals (depth: int) (expr: Expression) : string list =
    match expr.Kind with
    | FunctionCall("__for", _, [ iterable; Kind (Lambda(parameters, body)) ]) ->
        [ forLoopCursorLocal depth; forLoopEndLocal depth ]
        @ (parameters |> List.map (fun (parameter, _) -> forLoopElementLocal parameter depth))
        @ collectForLoopLocals depth iterable
        @ collectForLoopLocals (depth + 1) body
    | _ -> exprChildren expr |> List.collect (collectForLoopLocals depth)

let private interpolationLocalNames =
    [ "__interp_len"
      "__interp_base"
      "__interp_cursor"
      "__interp_ptr"
      "__interp_copy_len"
      "__interp_src"
      "__interp_tmp"
      "__interp_start"
      "__interp_lo"
      "__interp_hi"
      "__interp_swap" ]

/// Every local that lowering the interpolations in `expr` needs, or none when it has no interpolation.
let private interpolationLocalsFor (expr: Expression) : string list =
    if containsInterpolation expr then
        interpolationLocalNames
        @ [ for index in 0 .. maxInterpolationHoles expr - 1 -> interpolationHoleLocalName index ]
    else
        []

let private maxOrZero list =
    match list with
    | [] -> 0
    | _ -> List.max list

let rec private countMatchExpressions (expr: Expression) : int =
    match expr.Kind with
    | Match(values, arms) ->
        let arity = values.Length
        let maxArityInArms =
            arms |> List.map (fun (_, armExpr) -> countMatchArity armExpr) |> List.max
        arity + maxArityInArms
    | FunctionCall(_, _, args) -> args |> List.sumBy countMatchExpressions
    | BinaryOp(_, left, right) -> countMatchExpressions left + countMatchExpressions right
    | IfExpr(cond, thenExpr, elseExpr) ->
        countMatchExpressions cond + countMatchExpressions thenExpr + countMatchExpressions elseExpr
    | Lambda(_, body) -> countMatchExpressions body
    | Pipe(value, _, _, args) -> countMatchExpressions value + (args |> List.sumBy countMatchExpressions)
    | Block statements ->
        statements |> List.map _.Kind
        |> List.sumBy (function
            | DefStatement(_, _, _, rhs) -> countMatchExpressions rhs
            | ExprStatement value -> countMatchExpressions value
            | ImportStatement _
            | TypeDefStatement _
            | UseStatement _ -> 0)
    | TupleExpr values -> values |> List.sumBy countMatchExpressions
    | ListExpr values -> values |> List.sumBy countMatchExpressions
    | RecordExpr fields ->
        fields
        |> List.sumBy (function
            | NamedField(_, value) -> countMatchExpressions value
            | PositionalField value -> countMatchExpressions value)
    | UseIn(_, body) -> countMatchExpressions body
    | MemberAccess(value, _, _) -> countMatchExpressions value
    | ContextMemberAccess(_, _) -> 0
    | ContextMemberCall(_, _, args) -> args |> List.sumBy countMatchExpressions
    | TagExpr(_, payload) -> payload |> Option.map countMatchExpressions |> Option.defaultValue 0
    | InterpolatedString parts ->
        parts
        |> List.sumBy (function
            | StringText _ -> 0
            | StringExpr value -> countMatchExpressions value)
    | WorkflowBindExpr(_, value) -> countMatchExpressions value
    | WorkflowReturnExpr value -> countMatchExpressions value
    | UnitExpr -> 0
    | LiteralExpr _ -> 0
    | IdentifierExpr _ -> 0

and countMatchArity (expr: Expression) : int =
    match expr.Kind with
    | Match(values, arms) ->
        // A match inside an arm runs while the outer scrutinees are still held, so it needs its own temps on
        // top of the outer ones; sibling arms reuse the same temps.
        let arity = values.Length
        let maxArityInArms =
            arms |> List.map (fun (_, armExpr) -> countMatchArity armExpr) |> maxOrZero
        arity + maxArityInArms
    | FunctionCall(_, _, args) -> args |> List.map countMatchArity |> maxOrZero
    | BinaryOp(_, left, right) -> max (countMatchArity left) (countMatchArity right)
    | IfExpr(cond, thenExpr, elseExpr) ->
        [countMatchArity cond; countMatchArity thenExpr; countMatchArity elseExpr] |> List.max
    | Lambda(_, body) -> countMatchArity body
    | Pipe(value, _, _, args) -> max (countMatchArity value) (args |> List.map countMatchArity |> maxOrZero)
    | Block statements ->
        statements |> List.map _.Kind
        |> List.map (function
            | DefStatement(_, _, _, rhs) -> countMatchArity rhs
            | ExprStatement value -> countMatchArity value
            | ImportStatement _
            | TypeDefStatement _
            | UseStatement _ -> 0)
        |> maxOrZero
    | TupleExpr values
    | ListExpr values -> values |> List.map countMatchArity |> maxOrZero
    | RecordExpr fields ->
        fields
        |> List.map (function
            | NamedField(_, value) -> countMatchArity value
            | PositionalField value -> countMatchArity value)
        |> maxOrZero
    | UseIn(_, body) -> countMatchArity body
    | MemberAccess(value, _, _) -> countMatchArity value
    | TagExpr(_, payload) -> payload |> Option.map countMatchArity |> Option.defaultValue 0
    | InterpolatedString parts ->
        parts
        |> List.map (function
            | StringText _ -> 0
            | StringExpr value -> countMatchArity value)
        |> maxOrZero
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> countMatchArity value
    | ContextMemberAccess(_, _) -> 0
    | ContextMemberCall(_, _, args) -> args |> List.map countMatchArity |> maxOrZero
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> 0

/// True when `predicate` holds for `expr` or any expression nested inside it.
let rec private existsExpr (predicate: Expression -> bool) (expr: Expression) : bool =
    let recurse = existsExpr predicate
    predicate expr
    || match expr.Kind with
       | FunctionCall(_, _, args) -> args |> List.exists recurse
       | BinaryOp(_, left, right) -> recurse left || recurse right
       | IfExpr(cond, thenExpr, elseExpr) -> recurse cond || recurse thenExpr || recurse elseExpr
       | Block statements -> existsStatements predicate statements
       | Lambda(_, body) -> recurse body
       | Pipe(value, _, _, args) -> recurse value || (args |> List.exists recurse)
       | TupleExpr items
       | ListExpr items -> items |> List.exists recurse
       | RecordExpr fields ->
           fields
           |> List.exists (function
               | NamedField(_, value) -> recurse value
               | PositionalField value -> recurse value)
       | Match(values, arms) ->
           (values |> List.exists recurse) || (arms |> List.exists (fun (_, armExpr) -> recurse armExpr))
       | UseIn(_, body) -> recurse body
       | MemberAccess(inner, _, _) -> recurse inner
       | TagExpr(_, payload) -> payload |> Option.exists recurse
       | InterpolatedString parts ->
           parts
           |> List.exists (function
               | StringText _ -> false
               | StringExpr inner -> recurse inner)
       | WorkflowBindExpr(_, value)
       | WorkflowReturnExpr value -> recurse value
       | ContextMemberCall(_, _, args) -> args |> List.exists recurse
       | ContextMemberAccess(_, _)
       | UnitExpr
       | LiteralExpr _
       | IdentifierExpr _ -> false

and private existsStatements (predicate: Expression -> bool) (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> existsExpr predicate rhs
        | ExprStatement expr -> existsExpr predicate expr
        | UseStatement (UseValue value)
        | UseStatement (UseContextInstance(_, value)) -> existsExpr predicate value
        | ImportStatement _
        | TypeDefStatement _ -> false)

let rec private containsDbgCallExpr (expr: Expression) : bool =
    match expr.Kind with
    | FunctionCall(name, _, _) when name = "dbg" -> true
    | FunctionCall(_, _, args) -> args |> List.exists containsDbgCallExpr
    | BinaryOp(_, left, right) -> containsDbgCallExpr left || containsDbgCallExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsDbgCallExpr cond || containsDbgCallExpr thenExpr || containsDbgCallExpr elseExpr
    | Block statements -> containsDbgCallStatements statements
    | Lambda(_, body) -> containsDbgCallExpr body
    | Pipe(value, _, _, args) -> containsDbgCallExpr value || (args |> List.exists containsDbgCallExpr)
    | TupleExpr items
    | ListExpr items -> items |> List.exists containsDbgCallExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsDbgCallExpr value
            | PositionalField value -> containsDbgCallExpr value)
    | Match(values, arms) ->
        (values |> List.exists containsDbgCallExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsDbgCallExpr armExpr))
    | UseIn(_, body) -> containsDbgCallExpr body
    | MemberAccess(inner, _, _) -> containsDbgCallExpr inner
    | TagExpr(_, payload) -> payload |> Option.exists containsDbgCallExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr inner -> containsDbgCallExpr inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsDbgCallExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsDbgCallExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsDbgCallStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> containsDbgCallExpr rhs
        | ExprStatement expr -> containsDbgCallExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private usesHeapAllocationExpr (expr: Expression) : bool =
    match expr.Kind with
    | TupleExpr _
    | ListExpr _
    | RecordExpr _
    | FunctionCall("__closure", _, _) -> true
    | FunctionCall(_, _, args) -> flattenCallArgs args |> List.exists usesHeapAllocationExpr
    | BinaryOp(_, left, right) -> usesHeapAllocationExpr left || usesHeapAllocationExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        usesHeapAllocationExpr cond || usesHeapAllocationExpr thenExpr || usesHeapAllocationExpr elseExpr
    | Block statements -> usesHeapAllocationStatements statements
    | Lambda(_, body) -> usesHeapAllocationExpr body
    | Pipe(value, _, _, args) -> usesHeapAllocationExpr value || (flattenCallArgs args |> List.exists usesHeapAllocationExpr)
    | Match(values, arms) ->
        (values |> List.exists usesHeapAllocationExpr)
        || (arms |> List.exists (fun (_, armExpr) -> usesHeapAllocationExpr armExpr))
    | UseIn(_, body) -> usesHeapAllocationExpr body
    | MemberAccess(inner, _, _) -> usesHeapAllocationExpr inner
    | TagExpr(_, payload) -> payload.IsSome
    | InterpolatedString _ -> true
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> usesHeapAllocationExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists usesHeapAllocationExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private usesHeapAllocationStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> usesHeapAllocationExpr rhs
        | ExprStatement expr -> usesHeapAllocationExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private collectDbgTagNamesExpr (expr: Expression) : string list =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(tagName, None))] -> [ tagName ]
        | _ -> []
    | FunctionCall(_, _, args) -> args |> List.collect collectDbgTagNamesExpr
    | BinaryOp(_, left, right) -> collectDbgTagNamesExpr left @ collectDbgTagNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectDbgTagNamesExpr cond @ collectDbgTagNamesExpr thenExpr @ collectDbgTagNamesExpr elseExpr
    | Lambda(_, body) -> collectDbgTagNamesExpr body
    | Pipe(value, _, _, args) -> collectDbgTagNamesExpr value @ (args |> List.collect collectDbgTagNamesExpr)
    | Block statements -> collectDbgTagNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectDbgTagNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectDbgTagNamesExpr value
            | PositionalField value -> collectDbgTagNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values |> List.collect collectDbgTagNamesExpr
        let armTags = arms |> List.collect (fun (_, armExpr) -> collectDbgTagNamesExpr armExpr)
        valueTags @ armTags
    | UseIn(_, body) -> collectDbgTagNamesExpr body
    | MemberAccess(value, _, _) -> collectDbgTagNamesExpr value
    | TagExpr(_, payload) -> payload |> Option.map collectDbgTagNamesExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectDbgTagNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectDbgTagNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectDbgTagNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectStringLiteralsExpr (expr: Expression) : string list =
    match expr.Kind with
    | LiteralExpr(StringLit value) -> [ value ]
    | Block statements -> collectStringLiteralsStatements statements
    | BinaryOp(_, left, right) -> collectStringLiteralsExpr left @ collectStringLiteralsExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectStringLiteralsExpr cond @ collectStringLiteralsExpr thenExpr @ collectStringLiteralsExpr elseExpr
    | FunctionCall(_, _, args) -> args |> List.collect collectStringLiteralsExpr
    | Lambda(_, body) -> collectStringLiteralsExpr body
    | Pipe(value, _, _, args) -> collectStringLiteralsExpr value @ (args |> List.collect collectStringLiteralsExpr)
    | TupleExpr items
    | ListExpr items -> items |> List.collect collectStringLiteralsExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectStringLiteralsExpr value
            | PositionalField value -> collectStringLiteralsExpr value)
    | Match(values, arms) ->
        let valueStrings = values |> List.collect collectStringLiteralsExpr
        let armStrings =
            arms
            |> List.collect (fun (patterns, armExpr) ->
                (patterns |> List.collect collectStringLiteralsPattern) @ collectStringLiteralsExpr armExpr)
        valueStrings @ armStrings
    | UseIn(_, body) -> collectStringLiteralsExpr body
    | MemberAccess(inner, _, _) -> collectStringLiteralsExpr inner
    | TagExpr(_, payload) -> payload |> Option.map collectStringLiteralsExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        // Literals inside `{...}` holes need data-segment offsets like any other literal.
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectStringLiteralsExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectStringLiteralsExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectStringLiteralsExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// String literals that appear inside patterns (`| "Sat" -> ...`, `| #name("x") -> ...`) need data-segment
/// offsets just like literals in expressions.
and private collectStringLiteralsPattern (pattern: Pattern) : string list =
    match pattern.Kind with
    | LiteralPattern(StringLit value) -> [ value ]
    | TagPattern(_, Some payload) -> collectStringLiteralsPattern payload
    | TuplePattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect collectStringLiteralsPattern
    | ListSplatMiddle(beforePatterns, afterPatterns) ->
        (beforePatterns |> List.collect collectStringLiteralsPattern)
        @ (afterPatterns |> List.collect collectStringLiteralsPattern)
    | RecordPattern(_, patterns) -> patterns |> List.collect collectStringLiteralsPattern
    | RecordMemberPattern members -> members |> List.collect (fun (_, p) -> collectStringLiteralsPattern p)
    | GuardPattern(_, value)
    | RangePattern(value, _) -> collectStringLiteralsExpr value
    | TagPattern(_, None)
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | ElsePattern -> []

and private collectStringLiteralsStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, rhs) -> collectStringLiteralsExpr rhs
        | ExprStatement expr -> collectStringLiteralsExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

and private collectDbgTagNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectDbgTagNamesExpr value
        | ExprStatement value -> collectDbgTagNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let rec private collectDbgTagPayloadNamesExpr (expr: Expression) : string list =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(tagName, Some _))] -> [ tagName ]
        | _ -> []
    | FunctionCall(_, _, args) -> args |> List.collect collectDbgTagPayloadNamesExpr
    | BinaryOp(_, left, right) -> collectDbgTagPayloadNamesExpr left @ collectDbgTagPayloadNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectDbgTagPayloadNamesExpr cond @ collectDbgTagPayloadNamesExpr thenExpr @ collectDbgTagPayloadNamesExpr elseExpr
    | Lambda(_, body) -> collectDbgTagPayloadNamesExpr body
    | Pipe(value, _, _, args) -> collectDbgTagPayloadNamesExpr value @ (args |> List.collect collectDbgTagPayloadNamesExpr)
    | Block statements -> collectDbgTagPayloadNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectDbgTagPayloadNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectDbgTagPayloadNamesExpr value
            | PositionalField value -> collectDbgTagPayloadNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values |> List.collect collectDbgTagPayloadNamesExpr
        let armTags = arms |> List.collect (fun (_, armExpr) -> collectDbgTagPayloadNamesExpr armExpr)
        valueTags @ armTags
    | UseIn(_, body) -> collectDbgTagPayloadNamesExpr body
    | MemberAccess(value, _, _) -> collectDbgTagPayloadNamesExpr value
    | TagExpr(_, payload) -> payload |> Option.map collectDbgTagPayloadNamesExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectDbgTagPayloadNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectDbgTagPayloadNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectDbgTagPayloadNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectDbgTagPayloadNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectDbgTagPayloadNamesExpr value
        | ExprStatement value -> collectDbgTagPayloadNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let rec private containsRegularDbgExpr (expr: Expression) : bool =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(_, None))] -> false
        | _ -> true
    | FunctionCall(_, _, args) -> args |> List.exists containsRegularDbgExpr
    | BinaryOp(_, left, right) -> containsRegularDbgExpr left || containsRegularDbgExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsRegularDbgExpr cond || containsRegularDbgExpr thenExpr || containsRegularDbgExpr elseExpr
    | Lambda(_, body) -> containsRegularDbgExpr body
    | Pipe(value, _, _, args) -> containsRegularDbgExpr value || (args |> List.exists containsRegularDbgExpr)
    | Block statements -> containsRegularDbgStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.exists containsRegularDbgExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsRegularDbgExpr value
            | PositionalField value -> containsRegularDbgExpr value)
    | Match(values, arms) ->
        (values |> List.exists containsRegularDbgExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsRegularDbgExpr armExpr))
    | UseIn(_, body) -> containsRegularDbgExpr body
    | MemberAccess(value, _, _) -> containsRegularDbgExpr value
    | TagExpr(_, payload) -> payload |> Option.exists containsRegularDbgExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr value -> containsRegularDbgExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsRegularDbgExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsRegularDbgExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsDbgStringLiteralExpr (expr: Expression) : bool =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (LiteralExpr(StringLit _))] -> true
        | _ -> false
    | FunctionCall(_, _, args) -> args |> List.exists containsDbgStringLiteralExpr
    | BinaryOp(_, left, right) -> containsDbgStringLiteralExpr left || containsDbgStringLiteralExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsDbgStringLiteralExpr cond || containsDbgStringLiteralExpr thenExpr || containsDbgStringLiteralExpr elseExpr
    | Lambda(_, body) -> containsDbgStringLiteralExpr body
    | Pipe(value, _, _, args) -> containsDbgStringLiteralExpr value || (args |> List.exists containsDbgStringLiteralExpr)
    | Block statements -> statements |> List.map _.Kind |> List.exists (function
        | DefStatement(_, _, _, rhs) -> containsDbgStringLiteralExpr rhs
        | ExprStatement expr -> containsDbgStringLiteralExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)
    | TupleExpr values
    | ListExpr values -> values |> List.exists containsDbgStringLiteralExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsDbgStringLiteralExpr value
            | PositionalField value -> containsDbgStringLiteralExpr value)
    | Match(values, arms) ->
        (values |> List.exists containsDbgStringLiteralExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsDbgStringLiteralExpr armExpr))
    | UseIn(_, body) -> containsDbgStringLiteralExpr body
    | MemberAccess(value, _, _) -> containsDbgStringLiteralExpr value
    | TagExpr(_, payload) -> payload |> Option.exists containsDbgStringLiteralExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr inner -> containsDbgStringLiteralExpr inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsDbgStringLiteralExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsDbgStringLiteralExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsRegularDbgStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, value) -> containsRegularDbgExpr value
        | ExprStatement value -> containsRegularDbgExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private collectTagNamesExpr (expr: Expression) : string list =
    match expr.Kind with
    | TagExpr(name, payload) ->
        match payload with
        | Some value -> name :: collectTagNamesExpr value
        | None -> [ name ]
    | BinaryOp(_, left, right) -> collectTagNamesExpr left @ collectTagNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectTagNamesExpr cond @ collectTagNamesExpr thenExpr @ collectTagNamesExpr elseExpr
    | FunctionCall(_, _, args) -> args |> List.collect collectTagNamesExpr
    | Lambda(_, body) -> collectTagNamesExpr body
    | Pipe(value, _, _, args) -> collectTagNamesExpr value @ (args |> List.collect collectTagNamesExpr)
    | Block statements -> collectTagNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectTagNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectTagNamesExpr value
            | PositionalField value -> collectTagNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values |> List.collect collectTagNamesExpr
        let armTags =
            arms
            |> List.collect (fun (patterns, armExpr) ->
                (patterns |> List.collect collectTagNamesPattern) @ (collectTagNamesExpr armExpr))
        valueTags @ armTags
    | UseIn(_, body) -> collectTagNamesExpr body
    | MemberAccess(value, _, _) -> collectTagNamesExpr value
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectTagNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectTagNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectTagNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectTagNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectTagNamesExpr value
        | ExprStatement value -> collectTagNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

and private collectTagNamesPattern (pattern: Pattern) : string list =
    match pattern.Kind with
    | TagPattern(tagName, payloadOpt) ->
        match payloadOpt with
        | Some payload -> tagName :: collectTagNamesPattern payload
        | None -> [ tagName ]
    | TuplePattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect collectTagNamesPattern
    | ListSplatMiddle(beforePatterns, afterPatterns) ->
        (beforePatterns |> List.collect collectTagNamesPattern)
        @ (afterPatterns |> List.collect collectTagNamesPattern)
    | RecordPattern(_, patterns) -> patterns |> List.collect collectTagNamesPattern
    | RecordMemberPattern members -> members |> List.collect (fun (_, p) -> collectTagNamesPattern p)
    | GuardPattern(_, value)
    | RangePattern(value, _) -> collectTagNamesExpr value
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | ElsePattern -> []

let rec private transpileExpressionUnplaced (env: TranspileEnv) (expr: Expression) : string =
    /// The pointer to a string literal's bytes in the data section.
    let stringLiteralCode (value: string) =
        match env.StringLiterals |> Map.tryFind value with
        | Some offset -> $"i32.const {offset}"
        | None -> failwith $"Internal error: missing string literal offset for {value}"

    /// Allocates a block of 4-byte slots and fills them with `values` in order, leaving the block's pointer on
    /// the stack. The block is reserved before any value is evaluated and the pointer stays on the operand
    /// stack throughout, so a value that allocates (a nested record, a string) cannot overwrite the block.
    let allocateSlots (values: Expression list) =
        env.UsedHelpers.Add Helpers.alloc |> ignore
        env.UsedHelpers.Add Helpers.recordSet |> ignore
        let stores =
            values
            |> List.mapi (fun index value ->
                $"{transpileExpression env value}\n    i32.const {index * 4}\n    call ${Helpers.recordSet}")
        ($"i32.const {values.Length * 4}\n    call ${Helpers.alloc}" :: stores) |> String.concat "\n    "

    /// Allocates a record with `fields` (name and value, in storage order): a slot for its layout's number, then a
    /// slot per field, leaving a pointer to the first field on the stack. The layout's number lets a field be found
    /// when only the record's having it is known (see `Helpers.openField`).
    let allocateRecord (fields: (string * Expression) list) =
        env.UsedHelpers.Add Helpers.alloc |> ignore
        env.UsedHelpers.Add Helpers.recordSet |> ignore
        let layoutId = recordLayoutId env (fields |> List.map fst)
        let stores =
            fields
            |> List.mapi (fun index (_, value) ->
                $"{transpileExpression env value}\n    i32.const {(index + 1) * 4}\n    call ${Helpers.recordSet}")
        [ $"i32.const {(fields.Length + 1) * 4}\n    call ${Helpers.alloc}"
          $"i32.const {layoutId}\n    i32.const 0\n    call ${Helpers.recordSet}"
          yield! stores
          "i32.const 4\n    i32.add" ]
        |> String.concat "\n    "

    // `call`, or `return_call` when `expr` is a call in tail position.
    let call = if env.TailCalls.Contains expr.Id then "return_call" else "call"

    // A call through a function value: `closureCode` pushes the closure block, whose first slot is the
    // function's table slot. The block is passed as the environment, ahead of the arguments.
    let callClosure (closureCode: string) (argsCode: string list) =
        let arity = argsCode.Length + 1
        env.FunctionTable.IndirectArities.Add arity |> ignore
        ([ closureCode ] @ argsCode @ [ closureCode; "i32.load"; $"{call}_indirect (type $ctx_fn_{arity})" ])
        |> String.concat "\n    "

    let renderStaticBytes bytes =
        bytes
        |> Array.toList
        |> List.map (fun b ->
            $"local.get $__interp_cursor\n    i32.const {int b}\n    i32.store8\n    local.get $__interp_cursor\n    i32.const 1\n    i32.add\n    local.set $__interp_cursor")
        |> String.concat "\n    "

    let renderIntLength exprCode =
        $"{exprCode}\n    local.set $__interp_tmp\n    local.get $__interp_tmp\n    i32.const 0\n    i32.lt_s\n    if\n      local.get $__interp_len\n      i32.const 1\n      i32.add\n      local.set $__interp_len\n      local.get $__interp_tmp\n      i32.const -1\n      i32.mul\n      local.set $__interp_tmp\n    end\n    local.get $__interp_tmp\n    i32.const 0\n    i32.eq\n    if\n      local.get $__interp_len\n      i32.const 1\n      i32.add\n      local.set $__interp_len\n    else\n      loop $interp_int_len\n        local.get $__interp_len\n        i32.const 1\n        i32.add\n        local.set $__interp_len\n        local.get $__interp_tmp\n        i32.const 10\n        i32.div_u\n        local.set $__interp_tmp\n        local.get $__interp_tmp\n        i32.const 0\n        i32.ne\n        br_if $interp_int_len\n      end\n    end"

    let renderIntWrite exprCode =
        $"{exprCode}\n    local.set $__interp_tmp\n    local.get $__interp_tmp\n    i32.const 0\n    i32.lt_s\n    if\n      local.get $__interp_cursor\n      i32.const 45\n      i32.store8\n      local.get $__interp_cursor\n      i32.const 1\n      i32.add\n      local.set $__interp_cursor\n      local.get $__interp_tmp\n      i32.const -1\n      i32.mul\n      local.set $__interp_tmp\n    end\n    local.get $__interp_tmp\n    i32.const 0\n    i32.eq\n    if\n      local.get $__interp_cursor\n      i32.const 48\n      i32.store8\n      local.get $__interp_cursor\n      i32.const 1\n      i32.add\n      local.set $__interp_cursor\n    else\n      local.get $__interp_cursor\n      local.set $__interp_start\n      loop $interp_digits\n        local.get $__interp_cursor\n        local.get $__interp_tmp\n        i32.const 10\n        i32.rem_u\n        i32.const 48\n        i32.add\n        i32.store8\n        local.get $__interp_cursor\n        i32.const 1\n        i32.add\n        local.set $__interp_cursor\n        local.get $__interp_tmp\n        i32.const 10\n        i32.div_u\n        local.set $__interp_tmp\n        local.get $__interp_tmp\n        i32.const 0\n        i32.ne\n        br_if $interp_digits\n      end\n      local.get $__interp_start\n      local.set $__interp_lo\n      local.get $__interp_cursor\n      i32.const 1\n      i32.sub\n      local.set $__interp_hi\n      block $interp_rev_done\n        loop $interp_rev\n          local.get $__interp_lo\n          local.get $__interp_hi\n          i32.ge_u\n          br_if $interp_rev_done\n          local.get $__interp_lo\n          i32.load8_u\n          local.set $__interp_swap\n          local.get $__interp_lo\n          local.get $__interp_hi\n          i32.load8_u\n          i32.store8\n          local.get $__interp_hi\n          local.get $__interp_swap\n          i32.store8\n          local.get $__interp_lo\n          i32.const 1\n          i32.add\n          local.set $__interp_lo\n          local.get $__interp_hi\n          i32.const 1\n          i32.sub\n          local.set $__interp_hi\n          br $interp_rev\n        end\n      end\n    end"

    let renderStringCopy exprCode =
        $"{exprCode}\n    local.set $__interp_ptr\n    local.get $__interp_ptr\n    i32.const 4\n    i32.sub\n    i32.load\n    local.set $__interp_copy_len\n    local.get $__interp_ptr\n    local.set $__interp_src\n    block $interp_copy_done\n      loop $interp_copy\n        local.get $__interp_copy_len\n        i32.const 0\n        i32.eq\n        br_if $interp_copy_done\n        local.get $__interp_cursor\n        local.get $__interp_src\n        i32.load8_u\n        i32.store8\n        local.get $__interp_cursor\n        i32.const 1\n        i32.add\n        local.set $__interp_cursor\n        local.get $__interp_src\n        i32.const 1\n        i32.add\n        local.set $__interp_src\n        local.get $__interp_copy_len\n        i32.const 1\n        i32.sub\n        local.set $__interp_copy_len\n        br $interp_copy\n      end\n    end"

    let renderBoolLength exprCode =
        $"{exprCode}\n    if\n      local.get $__interp_len\n      i32.const 4\n      i32.add\n      local.set $__interp_len\n    else\n      local.get $__interp_len\n      i32.const 5\n      i32.add\n      local.set $__interp_len\n    end"

    let renderBoolWrite exprCode =
        let trueBytes = renderStaticBytes (Encoding.UTF8.GetBytes "true")
        let falseBytes = renderStaticBytes (Encoding.UTF8.GetBytes "false")
        $"{exprCode}\n    if\n      {trueBytes}\n    else\n      {falseBytes}\n    end"

    // Each `{...}` hole is evaluated exactly once, up front, into `__interp_hole_N`. The length and
    // write passes then only read that local. Evaluating a hole in the write pass would be wrong twice
    // over: its side effects would run again, and anything it allocated would land on top of the
    // output buffer, which is only reserved once the whole string has been written.
    // The type of a hole that may be a tag: a tag literal's is plain from the literal.
    let holeTy (inner: Expression) =
        match inner.Kind with
        | TagExpr(name, payload) ->
            let payloadTy = payload |> Option.map (fun p -> tryGetExprTypeName env p |> Option.map TyPrimitive |> Option.defaultValue (TyPrimitive "unknown"))
            Some (TyTag(name, payloadTy))
        | _ -> tryExprTy env inner

    let interpolationHoleType inner =
        match tryGetExprTypeName env inner with
        | Some ("string" | "int" | "bool" | "float" as typeName) -> typeName
        | Some other -> unsupportedAt inner.Span $"The WebAssembly backend cannot interpolate a value of type '{other}' yet"
        | None when holeTy inner |> Option.bind tryTagMembers |> Option.isSome -> "tag"
        | None ->
            // The backend works out a hole's type from what it can see, not from the type checker (#193).
            unsupportedAt inner.Span "The WebAssembly backend cannot tell the type of this interpolated value: annotate the function or value it comes from"

    // A tag hole prints as `#name`, or `#name(payload)` with a string payload in quotes, as the docs show
    // values. The discriminant picks the tag at run time; each tag the hole's type names is checked in turn.
    // A tag that only an open union's rest could hold has a payload type nobody here knows, so it prints
    // by name alone.
    let tagHoleCases (inner: Expression) =
        let members = holeTy inner |> Option.bind tryTagMembers |> Option.defaultValue []
        let named = members |> List.map fst |> Set.ofList
        let others =
            match holeTy inner |> Option.map Unifier.unalias with
            | Some (TyUnion options) when options |> List.exists (function TyVar _ -> true | _ -> false) ->
                env.TagIds |> Map.toList |> List.filter (fun (name, _) -> not (named.Contains name)) |> List.map (fun (name, _) -> name, None)
            | _ -> []
        (members @ others)
        |> List.map (fun (name, payloadTy) ->
            match env.TagIds |> Map.tryFind name with
            | Some id ->
                let payloadTypeName =
                    payloadTy
                    |> Option.map (fun ty ->
                        match trySimpleTypeName ty with
                        | Some ("string" | "int" | "bool" as typeName) -> typeName
                        | _ -> unsupportedAt inner.Span $"The WebAssembly backend cannot interpolate '#{name}' with a payload of type '{Unifier.tyToString ty}' yet")
                name, tagCode id, payloadTypeName
            | None -> failwith $"Internal error: missing tag discriminant for #{name}")

    let renderTagHole (valueCode: string) (inner: Expression) (renderText: string -> string) (renderPayload: string -> string -> string) =
        env.UsedHelpers.Add Helpers.tagCode |> ignore
        tagHoleCases inner
        |> List.map (fun (name, code, payloadTypeName) ->
            let body =
                match payloadTypeName with
                | None -> renderText $"#{name}"
                | Some typeName ->
                    let payloadCode = $"{valueCode}\n    i32.load offset=4"
                    let opening = renderText ("#" + name + "(")
                    let closing = renderText ")"
                    $"{opening}\n    {renderPayload typeName payloadCode}\n    {closing}"
            $"{valueCode}\n    call ${Helpers.tagCode}\n    i32.const {code}\n    i32.eq\n    if\n      {body}\n    end")
        |> String.concat "\n    "

    let isTagOperand (operand: Expression) =
        match operand.Kind with
        | TagExpr _ -> true
        | _ -> holeTy operand |> Option.bind tryTagMembers |> Option.isSome

    let renderTextLength (text: string) =
        $"local.get $__interp_len\n    i32.const {Encoding.UTF8.GetByteCount text}\n    i32.add\n    local.set $__interp_len"

    let renderTextWrite (text: string) = renderStaticBytes (Encoding.UTF8.GetBytes text)

    let renderTagPayloadLength typeName payloadCode =
        match typeName with
        | "string" ->
            let quotes = renderTextLength "\"\""
            $"{quotes}\n    {payloadCode}\n    i32.const 4\n    i32.sub\n    i32.load\n    local.get $__interp_len\n    i32.add\n    local.set $__interp_len"
        | "int" -> renderIntLength payloadCode
        | _ -> renderBoolLength payloadCode

    let renderTagPayloadWrite typeName payloadCode =
        match typeName with
        | "string" ->
            let quote = renderTextWrite "\""
            $"{quote}\n    {renderStringCopy payloadCode}\n    {quote}"
        | "int" -> renderIntWrite payloadCode
        | _ -> renderBoolWrite payloadCode

    // A float hole is formatted to a string here, in the evaluation pass, since formatting allocates and
    // the length and write passes must not.
    let renderInterpolationHoleEval (index: int) (inner: Expression) =
        let valueCode =
            match interpolationHoleType inner with
            | "float" ->
                env.UsedHelpers.Add Helpers.floatToString |> ignore
                $"{transpileExpression env inner}\n    call ${Helpers.floatToString}"
            | _ -> transpileExpression env inner
        $"{valueCode}\n    local.set ${interpolationHoleLocalName index}"

    let renderInterpolationPartLength (part, holeIndex) =
        match part with
        | StringText text ->
            let len = Encoding.UTF8.GetByteCount text
            $"local.get $__interp_len\n    i32.const {len}\n    i32.add\n    local.set $__interp_len"
        | StringExpr inner ->
            let valueCode = $"local.get ${interpolationHoleLocalName holeIndex}"
            match interpolationHoleType inner with
            | "string" | "float" ->
                $"{valueCode}\n    i32.const 4\n    i32.sub\n    i32.load\n    local.get $__interp_len\n    i32.add\n    local.set $__interp_len"
            | "int" -> renderIntLength valueCode
            | "tag" -> renderTagHole valueCode inner renderTextLength renderTagPayloadLength
            | _ -> renderBoolLength valueCode

    let renderInterpolationPartWrite (part, holeIndex) =
        match part with
        | StringText text -> renderStaticBytes (Encoding.UTF8.GetBytes text)
        | StringExpr inner ->
            let valueCode = $"local.get ${interpolationHoleLocalName holeIndex}"
            match interpolationHoleType inner with
            | "string" | "float" -> renderStringCopy valueCode
            | "int" -> renderIntWrite valueCode
            | "tag" -> renderTagHole valueCode inner renderTextWrite renderTagPayloadWrite
            | _ -> renderBoolWrite valueCode

    match expr.Kind with
    | UnitExpr ->
        "i32.const 0"
    | LiteralExpr(IntLit value) ->
        $"i32.const {value}"
    | LiteralExpr(FloatLit value) ->
        env.UsedHelpers.Add Helpers.floatBox |> ignore
        $"f64.const {wasmFloatConst value}\n    call ${Helpers.floatBox}"
    | LiteralExpr(BoolLit value) ->
        if value then "i32.const 1" else "i32.const 0"
    | LiteralExpr(StringLit value) -> stringLiteralCode value
    | InterpolatedString parts ->
        // Pair every part with the index of the hole local it reads (text parts get a dummy index).
        let indexedParts =
            parts
            |> List.fold (fun (acc, nextHole) part ->
                match part with
                | StringExpr _ -> ((part, nextHole) :: acc, nextHole + 1)
                | StringText _ -> ((part, -1) :: acc, nextHole)) ([], 0)
            |> fst
            |> List.rev
        let evalCode =
            indexedParts
            |> List.choose (function
                | (StringExpr inner, holeIndex) -> Some (renderInterpolationHoleEval holeIndex inner)
                | _ -> None)
            |> String.concat "\n    "
        let lengthCode = indexedParts |> List.map renderInterpolationPartLength |> String.concat "\n    "
        let writeCode = indexedParts |> List.map renderInterpolationPartWrite |> String.concat "\n    "
        $"{evalCode}\n    i32.const 0\n    local.set $__interp_len\n    {lengthCode}\n    global.get $heap_ptr\n    local.tee $__interp_base\n    local.get $__interp_len\n    i32.store\n    local.get $__interp_base\n    i32.const 4\n    i32.add\n    local.set $__interp_cursor\n    {writeCode}\n    local.get $__interp_cursor\n    i32.const 0\n    i32.store8\n    local.get $__interp_base\n    local.get $__interp_len\n    i32.const 8\n    i32.add\n    i32.const -4\n    i32.and\n    i32.add\n    global.set $heap_ptr\n    local.get $__interp_base\n    i32.const 4\n    i32.add"
    | IdentifierExpr name ->
        match env.Locals |> Map.tryFind name with
        | Some localName -> $"local.get ${localName}"
        | None when env.KnownFunctions.Contains name ->
            match env.FunctionArities |> Map.tryFind name with
            | Some arity when arity > 0 ->
                // A function with parameters, referenced rather than called: the value is its table slot.
                if env.FunctionContextRequirements.ContainsKey name then
                    failwith $"Unsupported for WASM MVP backend: '{name}' requires contexts and cannot be used as a value"
                $"i32.const {functionValueSlot env name arity}"
            | _ ->
                // A top-level value (or a parameterless function): evaluate it.
                $"call ${sanitizeIdentifier name}"
        | None -> failwith $"The WebAssembly backend cannot compile a reference to '{name}' here yet"
    | TagExpr(name, None) ->
        match env.TagIds |> Map.tryFind name with
        | Some id -> $"i32.const {tagCode id}"
        | None -> failwith $"Internal error: missing tag discriminant for #{name}"
    | TagExpr(name, Some payloadExpr) ->
        let code =
            match env.TagIds |> Map.tryFind name with
            | Some id -> tagCode id
            | None -> failwith $"Internal error: missing tag discriminant for #{name}"
        env.UsedHelpers.Add Helpers.tagBox |> ignore
        let payloadCode = transpileExpression env payloadExpr
        $"i32.const {code}\n    {payloadCode}\n    call ${Helpers.tagBox}"
    | BinaryOp("++", left, right) ->
        // Concatenation: the backend has to know whether it is joining strings or lists.
        let leftCode = transpileExpression env left
        let rightCode = transpileExpression env right
        let operandType =
            tryGetExprTypeName env left |> Option.orElseWith (fun () -> tryGetExprTypeName env right)
        match operandType with
        | Some "string" ->
            env.UsedHelpers.Add Helpers.stringConcat |> ignore
            $"{leftCode}\n    {rightCode}\n    call ${Helpers.stringConcat}"
        | Some "list" ->
            env.UsedHelpers.Add Helpers.listConcat |> ignore
            $"{leftCode}\n    {rightCode}\n    call ${Helpers.listConcat}"
        | Some other -> failwith $"Unsupported operand type for ++ in WASM MVP backend: {other}"
        | None -> failwith "The WebAssembly backend cannot tell whether this `++` joins strings or lists: annotate one side"
    | BinaryOp(op, left, right) when (op = "==" || op = "!=") && (isStringExpr env left || isStringExpr env right) ->
        // Strings are pointers, so equality calls into the runtime helper instead of comparing the pointers.
        let leftCode = transpileExpression env left
        let rightCode = transpileExpression env right
        match op with
        | "==" ->
            env.UsedHelpers.Add Helpers.stringEquals |> ignore
            $"{leftCode}\n    {rightCode}\n    call ${Helpers.stringEquals}"
        | _ ->
            env.UsedHelpers.Add Helpers.stringEquals |> ignore
            $"{leftCode}\n    {rightCode}\n    call ${Helpers.stringEquals}\n    i32.eqz"
    | BinaryOp(op, left, right) when (op = "==" || op = "!=") && (isTagOperand left || isTagOperand right) ->
        // A tag with a payload is a pointer to its cell, so equality compares the cells' contents.
        env.UsedHelpers.Add Helpers.tagEquals |> ignore
        let leftCode = transpileExpression env left
        let rightCode = transpileExpression env right
        let negate = if op = "!=" then "\n    i32.eqz" else ""
        $"{leftCode}\n    {rightCode}\n    call ${Helpers.tagEquals}{negate}"
    | BinaryOp(op, left, right) when tryGetExprTypeName env left = Some "float" || tryGetExprTypeName env right = Some "float" ->
        // Floats are boxed: unbox both operands, compute in f64, and box an arithmetic result.
        match wasmFloatInstruction op with
        | Some instr ->
            let leftCode = transpileExpression env left
            let rightCode = transpileExpression env right
            let compute = $"{leftCode}\n    f64.load\n    {rightCode}\n    f64.load\n    {instr}"
            if instr.EndsWith "eq" || instr.EndsWith "ne" || instr.EndsWith "lt" || instr.EndsWith "le" || instr.EndsWith "gt" || instr.EndsWith "ge" then
                compute
            else
                env.UsedHelpers.Add Helpers.floatBox |> ignore
                $"{compute}\n    call ${Helpers.floatBox}"
        | None ->
            failwith $"Unsupported binary operator on floats for WASM MVP backend: {op}"
    | BinaryOp(op, left, right) ->
        match wasmBinaryInstruction op with
        | Some instr ->
            let leftCode = transpileExpression env left
            let rightCode = transpileExpression env right
            $"{leftCode}\n    {rightCode}\n    {instr}"
        | None ->
            failwith $"Unsupported binary operator for WASM MVP backend: {op}"
    | ListExpr items ->
        // A list is 0 (empty) or a pointer to a cell of (head, tail). Evaluate the items left to right, push
        // the empty list, then cons from the back.
        env.UsedHelpers.Add Helpers.listCons |> ignore
        let itemCodes = items |> List.map (transpileExpression env)
        let consCalls = items |> List.map (fun _ -> $"call ${Helpers.listCons}")
        (itemCodes @ [ "i32.const 0" ] @ consCalls) |> String.concat "\n    "
    | IfExpr(cond, thenExpr, elseExpr) ->
        let condCode = transpileExpression env cond
        let thenCode = transpileExpression env thenExpr
        let elseCode = transpileExpression env elseExpr
        $"{condCode}\n    (if (result i32)\n      (then\n        {thenCode}\n      )\n      (else\n        {elseCode}\n      )\n    )"
    | TupleExpr items ->
        // A tuple is a record whose fields are its positions.
        allocateRecord (items |> List.mapi (fun index item -> string index, item))
    | RecordExpr fields ->
        // Sort fields alphabetically by name, store in that order on heap
        let sortedFields =
            fields
            |> List.mapi (fun i field ->
                match field with
                | NamedField(name, expr) -> (name, expr)
                | PositionalField expr -> (string i, expr))
            |> List.sortBy fst
        // A record is a block of one 4-byte slot per field, in field-name order.
        allocateRecord sortedFields
    | MemberAccess(recordExpr, fieldName, _) when isOpenRecordExpr env recordExpr ->
        // Where the field is depends on the record: look it up by the layout the record carries.
        $"{transpileExpression env recordExpr}\n    i32.const {openFieldId env fieldName}\n    call ${Helpers.openField}"
    | MemberAccess(recordExpr, fieldName, _) ->
        // Support both numeric indices (t.0, t.1) and named fields (r.name)
        match System.Int32.TryParse(fieldName) with
        | true, index ->
            // Numeric index - direct offset calculation
            let recordCode = transpileExpression env recordExpr
            let offset = index * 4
            $"{recordCode}\n    i32.const {offset}\n    i32.add\n    i32.load"
        | false, _ ->
            // Named field: find its offset from the record's layout. The inferred type is the most general
            // source (it covers nested records, constructor results and top-level values); the syntactic
            // bookkeeping below remains for modules compiled without type information.
            let loadField (fieldNames: string list) =
                match fieldNames |> List.tryFindIndex ((=) fieldName) with
                | Some index ->
                    let recordCode = transpileExpression env recordExpr
                    let offset = index * 4
                    Some $"{recordCode}\n    i32.const {offset}\n    i32.add\n    i32.load"
                | None -> None
            match tryRecordLayoutOfExpr env recordExpr |> Option.bind loadField with
            | Some code -> code
            | None ->
            match recordExpr.Kind with
            | IdentifierExpr varName ->
                match env.RecordLayouts |> Map.tryFind varName with
                | Some fieldNames ->
                    match fieldNames |> List.tryFindIndex ((=) fieldName) with
                    | Some index ->
                        let recordCode = transpileExpression env recordExpr
                        let offset = index * 4
                        $"{recordCode}\n    i32.const {offset}\n    i32.add\n    i32.load"
                    | None ->
                        failwith $"Unknown field '{fieldName}' for record variable '{varName}'"
                | None ->
                    let qualifiedName = $"{varName}.{fieldName}"
                    if env.KnownFunctions.Contains qualifiedName then
                        $"call ${sanitizeIdentifier qualifiedName}"
                    else
                        failwith $"No record layout known for variable '{varName}' when accessing .{fieldName}"
            | RecordExpr fields ->
                // Inline record expression - compute layout directly
                let sortedFieldNames =
                    fields
                    |> List.mapi (fun i field ->
                        match field with
                        | NamedField(name, _) -> name
                        | PositionalField _ -> string i)
                    |> List.sort
                match sortedFieldNames |> List.tryFindIndex ((=) fieldName) with
                | Some index ->
                    let recordCode = transpileExpression env recordExpr
                    let offset = index * 4
                    $"{recordCode}\n    i32.const {offset}\n    i32.add\n    i32.load"
                | None ->
                    failwith $"Unknown field '{fieldName}' in inline record expression"
            | _ ->
                failwith $"Unsupported member access for WASM MVP backend: cannot determine layout for .{fieldName}"
    // ...existing code...
    | Match(values, arms) ->
        // Multi-scrutinee support
        let matchTempNames =
            env.MatchTempNames
            |> List.skip env.NextMatchTemp
            |> List.take values.Length
        if matchTempNames.Length <> values.Length then
            failwith "Internal error: not enough match temp locals for multi-scrutinee match"

        let envForNested = { env with NextMatchTemp = env.NextMatchTemp + values.Length }

        let scrutineeCodes = values |> List.map (transpileExpression envForNested)
        let scrutineeTys = values |> List.map (tryExprTy env)
        let setScrutineeLocals =
            List.zip matchTempNames scrutineeCodes
            |> List.map (fun (name, code) -> $"{code}\n    local.set ${name}")
            |> String.concat "\n    "

        let patternIsCatchAll pattern =
            match pattern with
            | WildcardPattern
            | ElsePattern
            | IdentifierPattern _ -> true
            | _ -> false

        // Patterns are lowered against a `valueCode` snippet that pushes the value being matched: the
        // scrutinee local at the top of an arm, and a load from it for a tuple element, list element or tag
        // payload below that. Every snippet is a pure load, so it is safe to emit it more than once.
        let listTailCode valueCode count =
            valueCode + String.replicate count "\n      i32.const 4\n      i32.add\n      i32.load"
        let listElementCode valueCode index =
            $"{listTailCode valueCode index}\n      i32.load"
        let tupleElementCode valueCode index =
            $"{valueCode}\n      i32.const {index * 4}\n      i32.add\n      i32.load"
        let tagPayloadCode valueCode =
            $"{valueCode}\n      i32.load offset=4"

        /// `first` and then, only if it held, `second` (so `second` may load through pointers that are
        /// only valid when `first` holds).
        let guarded first second =
            $"{first}\n      (if (result i32)\n        (then\n          {second})\n        (else i32.const 0))"
        let allOf conditions =
            match conditions |> List.filter (fun condition -> condition <> "i32.const 1") with
            | [] -> "i32.const 1"
            | [ single ] -> single
            | many -> (many |> String.concat "\n      ") + String.replicate (many.Length - 1) "\n      i32.and"

        let rec patternCondition (pattern: Pattern) valueCode =
            match pattern.Kind with
            | ListPattern(elements, restOpt) ->
                env.UsedHelpers.Add Helpers.listLength |> ignore
                let lengthInstr = if restOpt.IsSome then "i32.ge_s" else "i32.eq"
                let lengthCheck =
                    $"{valueCode}\n      call ${Helpers.listLength}\n      i32.const {elements.Length}\n      {lengthInstr}"
                let elementChecks =
                    elements
                    |> List.mapi (fun index element -> patternCondition element (listElementCode valueCode index))
                    |> allOf
                let restCheck =
                    match restOpt with
                    | Some rest -> patternCondition rest (listTailCode valueCode elements.Length)
                    | None -> "i32.const 1"
                // Elements only exist once the length check holds.
                match allOf [ elementChecks; restCheck ] with
                | "i32.const 1" -> lengthCheck
                | elementsAndRest -> guarded lengthCheck elementsAndRest
            | TuplePattern patterns ->
                patterns
                |> List.mapi (fun index element -> patternCondition element (tupleElementCode valueCode index))
                |> allOf
            // `$Email(raw)`: a nominal value is its shape's value, which the payload pattern matches.
            | RecordPattern(typeName, [ payload ]) when isNominalTypeName typeName -> patternCondition payload valueCode
            | LiteralPattern(IntLit value) ->
                $"{valueCode}\n      i32.const {value}\n      i32.eq"
            | LiteralPattern(BoolLit value) ->
                let asInt = if value then 1 else 0
                $"{valueCode}\n      i32.const {asInt}\n      i32.eq"
            | LiteralPattern(StringLit value) ->
                env.UsedHelpers.Add Helpers.stringEquals |> ignore
                $"{valueCode}\n      {stringLiteralCode value}\n      call ${Helpers.stringEquals}"
            | TagPattern(tagName, payloadOpt) ->
                let code =
                    match env.TagIds |> Map.tryFind tagName with
                    | Some id -> tagCode id
                    | None -> failwith $"Internal error: missing tag discriminant for pattern #{tagName}"
                env.UsedHelpers.Add Helpers.tagCode |> ignore
                let discriminantCheck =
                    $"{valueCode}\n      call ${Helpers.tagCode}\n      i32.const {code}\n      i32.eq"
                match payloadOpt with
                | None -> discriminantCheck
                | Some payload ->
                    // Only look at the payload once the code matches: only then is the value a cell.
                    match patternCondition payload (tagPayloadCode valueCode) with
                    | "i32.const 1" -> discriminantCheck
                    | payloadCheck -> guarded discriminantCheck payloadCheck
            | GuardPattern(op, guardExpr) ->
                match wasmBinaryInstruction op with
                | Some instr ->
                    let guardCode = transpileExpression env guardExpr
                    $"{valueCode}\n      {guardCode}\n      {instr}"
                | None ->
                    failwith $"Unsupported guard operator for WASM MVP backend: {op}"
            | WildcardPattern
            | ElsePattern
            | IdentifierPattern _ -> "i32.const 1"
            | _ -> unsupportedAt pattern.Span "The WebAssembly backend cannot compile this kind of pattern yet"

        let rec emitArms remainingArms =
            match remainingArms with
            | [] -> "unreachable"
            | (patterns, armExpr) :: rest ->
                if List.length patterns <> values.Length then
                    failwith "Unsupported match arm: pattern count does not match scrutinee count"
                let conds : string list =
                    List.zip patterns matchTempNames
                    |> List.map (fun (pat, temp) -> patternCondition pat $"local.get ${temp}")
                let allConds =
                    match conds with
                    | [] -> "i32.const 1"
                    | [single] -> single
                    | many ->
                        let many : string list = many
                        let n = many.Length
                        many |> String.concat "\n      " |> fun s -> $"{s}\n      " + (List.init (n - 1) (fun _ -> "i32.and") |> String.concat "\n      ")
                // Add identifier payloads to envForNested.Locals before binding
                let payloadIdents = patterns |> List.collect patternBoundIdentifiers
                let envWithPayloadLocals =
                    payloadIdents
                    |> List.fold (fun acc ident ->
                        let localName = $"__payload_{ident}"
                        { acc with Locals = acc.Locals |> Map.add ident localName }) envForNested
                // A bare identifier pattern (`| n -> ...`, `| 0, y -> ...`) needs no local of its own: it
                // is an alias for the scrutinee temp it was matched against.
                let envWithPayloadLocals =
                    List.zip patterns matchTempNames
                    |> List.fold (fun acc (pat, temp) ->
                        match pat.Kind with
                        | IdentifierPattern ident -> { acc with Locals = acc.Locals |> Map.add ident temp }
                        | _ -> acc) envWithPayloadLocals
                // The names the patterns bind take their types from the scrutinees', so `a.x` works on a
                // record bound by `#some(a)`. A name whose type is unknown must still shadow an outer value
                // of the same name.
                let boundTys =
                    List.zip patterns scrutineeTys
                    |> List.collect (fun (pat, tyOpt) ->
                        match tyOpt with
                        | Some ty -> patternBoundTys pat ty
                        | None -> [])
                    |> Map.ofList
                let envWithPayloadLocals =
                    patterns
                    |> List.collect patternBoundIdentifiers
                    |> List.fold (fun acc ident ->
                        match boundTys |> Map.tryFind ident with
                        | Some ty ->
                            { acc with
                                ValueTys = acc.ValueTys |> Map.add ident ty
                                ValueTypes =
                                    match trySimpleTypeName ty with
                                    | Some tyName -> acc.ValueTypes |> Map.add ident tyName
                                    | None -> acc.ValueTypes |> Map.remove ident }
                        | None -> { acc with ValueTys = acc.ValueTys |> Map.remove ident }) envWithPayloadLocals
                let payloadLocal ident =
                    match envWithPayloadLocals.Locals |> Map.tryFind ident with
                    | Some localName -> localName
                    | None -> failwith $"Internal error: missing local for payload variable {ident}"
                // Stores for the identifiers nested inside a pattern, mirroring `patternCondition`.
                let rec patternBinds (pattern: Pattern) valueCode =
                    match pattern.Kind with
                    | IdentifierPattern ident -> [ $"{valueCode}\n        local.set ${payloadLocal ident}" ]
                    | TagPattern(_, Some payload) -> patternBinds payload (tagPayloadCode valueCode)
                    | ListPattern(elements, restOpt) ->
                        let elementBinds =
                            elements
                            |> List.mapi (fun index element -> patternBinds element (listElementCode valueCode index))
                            |> List.concat
                        let restBinds =
                            match restOpt with
                            | Some rest -> patternBinds rest (listTailCode valueCode elements.Length)
                            | None -> []
                        elementBinds @ restBinds
                    | TuplePattern elements ->
                        elements
                        |> List.mapi (fun index element -> patternBinds element (tupleElementCode valueCode index))
                        |> List.concat
                    | RecordPattern(typeName, [ payload ]) when isNominalTypeName typeName -> patternBinds payload valueCode
                    | _ -> []
                let patternPayloadBinds =
                    List.zip patterns matchTempNames
                    |> List.collect (fun (pat, temp) ->
                        match pat.Kind with
                        | IdentifierPattern _ -> []  // aliases the scrutinee local
                        | _ -> patternBinds pat $"local.get ${temp}")
                    |> String.concat "\n    "
                let armCode =
                    if patternPayloadBinds = "" then
                        transpileExpression envWithPayloadLocals armExpr
                    else
                        $"{patternPayloadBinds}\n    {transpileExpression envWithPayloadLocals armExpr}"
                let elseCode = emitArms rest
                $"{allConds}\n    (if (result i32)\n      (then\n        {armCode}\n      )\n      (else\n        {elseCode}\n      )\n    )"

        let armsCode = emitArms arms
        $"{setScrutineeLocals}\n    {armsCode}"
    // ...existing code...
    | FunctionCall("not", _, [ operand ]) ->
        $"{transpileExpression env operand}\n    i32.eqz"
    | FunctionCall("__with", _, [ baseExpr; Kind (RecordExpr updates) ]) ->
        // `record with f = v, ...`: copy the record's block, layout slot and all, then overwrite the updated fields
        // in place. The record is evaluated once and the copy's pointer stays on the stack, so the values may
        // allocate.
        let layout =
            tryRecordLayoutOfExpr env baseExpr
            |> Option.orElseWith (fun () ->
                match baseExpr.Kind with
                | IdentifierExpr name -> env.RecordLayouts |> Map.tryFind name
                | _ -> None)
        match layout with
        | None -> failwith "Unsupported for WASM MVP backend: `with` on a value whose record layout is not known"
        | Some fieldNames ->
            env.UsedHelpers.Add Helpers.recordCopy |> ignore
            env.UsedHelpers.Add Helpers.recordSet |> ignore
            let stores =
                updates
                |> List.map (function
                    | NamedField(fieldName, value) ->
                        match fieldNames |> List.tryFindIndex ((=) fieldName) with
                        | Some index -> $"{transpileExpression env value}\n    i32.const {(index + 1) * 4}\n    call ${Helpers.recordSet}"
                        | None -> failwith $"Unknown field '{fieldName}' in record update"
                    | PositionalField _ -> failwith "Unsupported for WASM MVP backend: positional field in a record update")
            [ $"{transpileExpression env baseExpr}\n    i32.const 4\n    i32.sub\n    i32.const {(fieldNames.Length + 1) * 4}\n    call ${Helpers.recordCopy}"
              yield! stores
              "i32.const 4\n    i32.add" ]
            |> String.concat "\n    "
    | FunctionCall("__range", _, _) ->
        failwith "Unsupported for WASM MVP backend: a range is only supported as the iterable of a for loop"
    | FunctionCall("__closure", _, fields) ->
        // A closure block: the function's table slot followed by the values it captures.
        allocateSlots fields
    | FunctionCall("__slot", _, [ Kind (IdentifierExpr name) ]) ->
        // The table slot of a lifted function, for the first field of its closure block.
        match env.FunctionArities |> Map.tryFind name with
        | Some arity -> $"i32.const {functionValueSlot env name arity}"
        | None -> failwith $"Internal error: no lifted function named {name}"
    | FunctionCall("__for", _, [ iterable; Kind (Lambda([ (parameter, _) ], body)) ]) ->
        // `for iterable | arms`: a loop with the body inlined. The element goes into a local the body reads
        // through the lambda's parameter name; a range counts from its start to its end, a list walks its
        // cells. The loop is unit.
        let depth = env.ForDepth
        let cursor = forLoopCursorLocal depth
        let finish = forLoopEndLocal depth
        let element = forLoopElementLocal parameter depth
        let doneLabel = $"__for_done_{depth}"
        let nextLabel = $"__for_next_{depth}"
        let elementTy, elementTypeName =
            match iterable.Kind with
            | FunctionCall("__range", _, _) -> Some (TyPrimitive "int"), Some "int"
            | _ ->
                let ty = tryExprTy env iterable |> Option.bind tryListElementType
                ty, ty |> Option.bind trySimpleTypeName
        // The body is usually `match element | pattern -> ...`: tell the backend the types of the names the
        // patterns bind, since the typed AST does not reach into a call's lambda argument.
        let patternValueTypes =
            match body.Kind, elementTy with
            | Match(_, arms), Some ty ->
                arms |> List.collect (fun (patterns, _) -> patterns |> List.collect (fun p -> patternBoundValueTypes { Pattern = p; Type = ty }))
            | _ -> []
        let bodyEnv =
            { env with
                Locals = env.Locals |> Map.add parameter element
                ValueTypes =
                    (match elementTypeName with
                     | Some typeName -> env.ValueTypes |> Map.add parameter typeName
                     | None -> env.ValueTypes)
                    |> fun values -> patternValueTypes |> List.fold (fun acc (name, typeName) -> acc |> Map.add name typeName) values
                ValueTys =
                    match elementTy with
                    | Some ty -> env.ValueTys |> Map.add parameter ty
                    | None -> env.ValueTys
                ForDepth = depth + 1 }
        let bodyCode = transpileExpression bodyEnv body
        match iterable.Kind with
        | FunctionCall("__range", _, [ startExpr; endExpr ]) ->
            $"{transpileExpression env startExpr}\n    local.set ${element}\n    {transpileExpression env endExpr}\n    local.set ${finish}\n    block ${doneLabel}\n      loop ${nextLabel}\n        local.get ${element}\n        local.get ${finish}\n        i32.gt_s\n        br_if ${doneLabel}\n        {bodyCode}\n        drop\n        local.get ${element}\n        i32.const 1\n        i32.add\n        local.set ${element}\n        br ${nextLabel}\n      end\n    end\n    i32.const 0"
        | _ ->
            $"{transpileExpression env iterable}\n    local.set ${cursor}\n    block ${doneLabel}\n      loop ${nextLabel}\n        local.get ${cursor}\n        i32.eqz\n        br_if ${doneLabel}\n        local.get ${cursor}\n        i32.load\n        local.set ${element}\n        {bodyCode}\n        drop\n        local.get ${cursor}\n        i32.const 4\n        i32.add\n        i32.load\n        local.set ${cursor}\n        br ${nextLabel}\n      end\n    end\n    i32.const 0"
    | FunctionCall(name, _, args) when name = "dbg" ->
        let dbgLocal =
            match env.DbgTempLocal with
            | Some localName -> localName
            | None -> failwith "Internal error: dbg local is not configured for WASM backend"
        let argExpr =
            match flattenCallArgs args with
            | [single] -> single
            | _ -> failwith "dbg expects a single argument"
        let isStringTypedExpr expr =
            match tryGetExprTypeName env expr with
            | Some "string" -> true
            | _ -> false
        let argCode =
            match argExpr.Kind with
            | TagExpr(tagName, None) ->
                let tagId =
                    match env.TagIds |> Map.tryFind tagName with
                    | Some id -> id
                    | None -> failwith $"Internal error: missing tag discriminant for #{tagName}"
                let importFn = $"dbg_tag_{sanitizeImportSuffix tagName}"
                $"i32.const {tagCode tagId}\n    local.set ${dbgLocal}\n    call ${importFn}\n    local.get ${dbgLocal}"
            | TagExpr(tagName, Some payloadExpr) ->
                let tagId =
                    match env.TagIds |> Map.tryFind tagName with
                    | Some id -> id
                    | None -> failwith $"Internal error: missing tag discriminant for #{tagName}"
                let importFn = $"dbg_tag_payload_{sanitizeImportSuffix tagName}"
                env.UsedHelpers.Add Helpers.tagBox |> ignore
                let payloadCode = transpileExpression env payloadExpr
                $"{payloadCode}\n    local.tee ${dbgLocal}\n    call ${importFn}\n    i32.const {tagCode tagId}\n    local.get ${dbgLocal}\n    call ${Helpers.tagBox}"
            | LiteralExpr(BoolLit value) -> if value then "i32.const 1" else "i32.const 0"
            | _ -> transpileExpression env argExpr
        match argExpr.Kind with
        | TagExpr _ -> argCode
        | LiteralExpr(StringLit _) -> $"{argCode}\n    local.tee ${dbgLocal}\n    call $dbg_str\n    local.get ${dbgLocal}"
        | _ when isStringTypedExpr argExpr -> $"{argCode}\n    local.tee ${dbgLocal}\n    call $dbg_str\n    local.get ${dbgLocal}"
        | _ when tryGetExprTypeName env argExpr = Some "float" ->
            env.UsedHelpers.Add Helpers.floatToString |> ignore
            $"{argCode}\n    local.tee ${dbgLocal}\n    call ${Helpers.floatToString}\n    call $dbg_str\n    local.get ${dbgLocal}"
        | _ when holeTy argExpr |> Option.bind tryTagMembers |> Option.isSome ->
            // A tag value prints as `"{value}"` would; the hole local keeps the value to return.
            env.UsedHelpers.Add Helpers.alloc |> ignore
            let rendered = transpileExpression env { argExpr with Kind = InterpolatedString [ StringExpr argExpr ] }
            $"{rendered}\n    call $dbg_str\n    local.get ${interpolationHoleLocalName 0}"
        | _ -> $"{argCode}\n    local.tee ${dbgLocal}\n    call $dbg\n    local.get ${dbgLocal}"
    | FunctionCall(name, _, args) when env.NominalWrappers.Contains name ->
        // `$Money(pence)`: a nominal value over a shape that is not a record is that shape's value.
        match flattenCallArgs args with
        | [ single ] -> transpileExpression env single
        | several -> transpileExpression env (Expression.from expr (TupleExpr several))
    | FunctionCall(name, _, args) ->
        // Check if this is a type constructor call (e.g., Person(...))
        match env.TypeLayouts |> Map.tryFind name with
        | Some typeFieldNames ->
            // Treat as record expression - extract fields from args
            let flatArgs = flattenCallArgs args
            match flatArgs with
            | [Kind (RecordExpr fields)] ->
                // Sort fields alphabetically by name and store on heap
                let sortedFields =
                    fields
                    |> List.mapi (fun i field ->
                        match field with
                        | NamedField(fieldName, expr) -> (fieldName, expr)
                        | PositionalField expr -> (string i, expr))
                    |> List.sortBy fst
                allocateRecord sortedFields
            | _ ->
                failwith $"Type constructor {name} expects a record expression as argument"
        | None ->
            let flatArgs = flattenCallArgs args
            let expectedArity = env.FunctionArities |> Map.tryFind name |> Option.defaultValue flatArgs.Length
            
            // Check if we need to destructure a tuple/record argument
            let argsCode =
                match flatArgs with
                | [Kind (IdentifierExpr varName)] when expectedArity > 1 ->
                    // Single identifier passed to multi-param function - try to destructure
                    match env.RecordLayouts |> Map.tryFind varName with
                    | Some fieldNames when fieldNames.Length = expectedArity ->
                        // Generate code to load each field from the record
                        let localName = env.Locals |> Map.tryFind varName |> Option.defaultValue (sanitizeIdentifier varName)
                        fieldNames
                        |> List.mapi (fun i _ ->
                            let offset = i * 4
                            $"local.get ${localName}\n    i32.const {offset}\n    i32.add\n    i32.load")
                    | _ ->
                        // Not a known record, just pass it
                        flatArgs |> List.map (transpileExpression env)
                | [Kind (RecordExpr fields)] when expectedArity > 1 && fields.Length = expectedArity ->
                    // `f(a, step = 2)`: each parameter takes the field of its name, or else the positional field
                    // in its place, as inference matches them.
                    let positional = fields |> List.choose (function PositionalField e -> Some e | NamedField _ -> None)
                    let named = fields |> List.choose (function NamedField(n, e) -> Some (n, e) | PositionalField _ -> None) |> Map.ofList
                    let parameters = env.FunctionParameters |> Map.tryFind name |> Option.defaultValue []
                    parameters
                    |> List.mapi (fun index parameter ->
                        match named |> Map.tryFind parameter with
                        | Some e -> e
                        | None ->
                            match positional |> List.tryItem index with
                            | Some e -> e
                            | None -> failwith $"No argument for parameter {parameter} of {name}")
                    |> List.map (transpileExpression env)
                | _ when expectedArity = 1 && flatArgs.Length > 1 ->
                    // `f(a, b)` and `f((a, b))` parse alike; a one-parameter callee takes the whole tuple.
                    [ transpileExpression env (Expression.from expr (TupleExpr flatArgs)) ]
                | [] when expectedArity = 1 ->
                    // `f()` on a `() -> a` written `{ _ -> ... }`: its one parameter is the unit.
                    [ transpileExpression env (Expression.from expr UnitExpr) ]
                | _ ->
                    flatArgs |> List.map (transpileExpression env)
            
            match env.ContextMembers |> Map.tryFind name with
            | Some memberCode ->
                // A context member in scope: a function value like any other.
                callClosure memberCode argsCode
            | None when env.Locals.ContainsKey name || (env.KnownFunctions.Contains name && expectedArity = 0 && not flatArgs.IsEmpty) ->
                // Calling a parameter, local or top-level value that holds a function value.
                let closureCode =
                    match env.Locals |> Map.tryFind name with
                    | Some localName -> $"local.get ${localName}"
                    | None -> $"call ${sanitizeIdentifier name}"
                callClosure closureCode argsCode
            | None when NyxCompiler.Intrinsics.isIntrinsic name ->
                // A compiler intrinsic (only the standard library can call these): a runtime helper of the same
                // name. Every Nanyx expression leaves an i32, so a unit result is materialised as 0.
                env.UsedHelpers.Add name |> ignore
                let unitResult = if NyxCompiler.Intrinsics.returnsUnit name then "\n    i32.const 0" else ""
                let prefix = argsCode |> String.concat "\n    "
                if prefix = "" then $"call ${name}{unitResult}" else $"{prefix}\n    call ${name}{unitResult}"
            | None ->
                if not (env.KnownFunctions.Contains name) then
                    failwith $"The WebAssembly backend cannot compile a call to '{name}' yet"
                else
                    // Check if this function requires contexts - if so, pass them
                    let requiredCtxFns = env.FunctionContextRequirements |> Map.tryFind name |> Option.defaultValue []
                    
                    // Helper to find a compatible context for a required context function, excluding already-used ones
                    // Required might be "ToString_a_toString" (with type param), available might be "ToString_int_toString"
                    let findCompatibleContext (usedContexts: Set<string>) (requiredCtxFnName: string) : string option =
                        // Extract base context name and member from qualified key like "ToString_a_toString"
                        let parts = requiredCtxFnName.Split('_')
                        if parts.Length < 2 then None
                        else
                            let baseName = parts.[0]
                            let memberName = parts.[parts.Length - 1]
                            // Look for an available context with same base name and member name, not already used
                            env.ContextMembers
                            |> Map.toList
                            |> List.map fst
                            |> List.tryFind (fun availKey ->
                                not (usedContexts.Contains availKey) &&
                                let availParts = availKey.Split('_')
                                availParts.Length >= 2 &&
                                availParts.[0] = baseName &&
                                availParts.[availParts.Length - 1] = memberName)
                    
                    let closureOver wrapper =
                        transpileExpression env (Expression.from expr (FunctionCall("__closure", None, [ Expression.from expr (FunctionCall("__slot", None, [ Expression.from expr (IdentifierExpr wrapper) ])) ])))

                    // The member's closure: one in scope (a parameter or a `use`), else the ambient instance's.
                    let memberCodeFor (ctxFnName: string) (resolvedName: string) =
                        match env.ContextMembers |> Map.tryFind resolvedName, env.AmbientContextMembers |> Map.tryFind resolvedName with
                        | Some memberCode, _ -> memberCode
                        | None, Some wrapper -> closureOver wrapper
                        | None, None ->
                            let available =
                                (env.ContextMembers |> Map.toList |> List.map fst) @
                                (env.AmbientContextMembers |> Map.toList |> List.map fst)
                            let availableStr = String.concat ", " available
                            failwith $"Context function {ctxFnName} not available in current scope. Available: [{availableStr}]"

                    // Where the type checker found each context the callee requires, as each required member's
                    // instance, in the order of `requiredCtxFns`: none for a call it found nothing for.
                    let instances =
                        match env.ContextInstances |> Map.tryFind expr.Id, env.FunctionContextTypes |> Map.tryFind name with
                        | Some found, Some contextTypes when found.Length = contextTypes.Length ->
                            List.zip contextTypes found
                            |> List.collect (fun (ctxType, instance) ->
                                let baseName = match ctxType.Kind with TypeName n | TypeApply(n, _) -> n | _ -> "unknown"
                                env.TypeLayouts |> Map.tryFind baseName |> Option.defaultValue [] |> List.map (fun memberName -> memberName, instance))
                            |> fun (perMember: (string * ContextInstance option) list) -> if perMember.Length = requiredCtxFns.Length then Some perMember else None
                        | _ -> None

                    // Use fold to process required contexts while tracking which have been used
                    let (ctxArgsCode, _) =
                        requiredCtxFns
                        |> List.mapi (fun index ctxFnName -> ctxFnName, instances |> Option.map (fun perMember -> perMember.[index]))
                        |> List.fold (fun (codeAcc: string list, usedCtxs: Set<string>) (ctxFnName, instance) ->
                            match instance with
                            // An instance in scope, as written where it is: the members of a `use` or of the
                            // context a function's type declares.
                            | Some (memberName, Some (InstanceInScope written)) ->
                                let key = contextMemberKey (Some written) memberName
                                (codeAcc @ [ memberCodeFor ctxFnName key ], usedCtxs.Add key)
                            // An instance built from the attached definitions: each member is one of them.
                            | Some (memberName, Some (InstanceSynthesized members)) ->
                                let definition = members |> List.find (fun (name, _) -> name = memberName) |> snd
                                let code =
                                    match env.FunctionWrappers |> Map.tryFind definition with
                                    | Some wrapper -> closureOver wrapper
                                    | None -> $"call ${sanitizeIdentifier definition}"
                                (codeAcc @ [ code ], usedCtxs)
                            | _ ->
                                // Try exact match first, then try compatible match
                                let resolvedName =
                                    if env.ContextMembers.ContainsKey ctxFnName then ctxFnName
                                    else
                                        match findCompatibleContext usedCtxs ctxFnName with
                                        | Some compatible -> compatible
                                        | None -> ctxFnName  // Will fail below with better error
                                (codeAcc @ [ memberCodeFor ctxFnName resolvedName ], usedCtxs.Add resolvedName)) ([], Set.empty)
                    let allArgsCode = argsCode @ ctxArgsCode
                    match allArgsCode with
                    | [] -> $"{call} ${sanitizeIdentifier name}"
                    | _ ->
                        let prefix = allArgsCode |> String.concat "\n    "
                        $"{prefix}\n    {call} ${sanitizeIdentifier name}"
    | ContextMemberCall(ctxType, memberName, args) ->
        // Context member call: C(T).f(args) - the member of this specific context instantiation
        let memberKey = contextMemberKey (Some ctxType) memberName
        let argsCode = flattenCallArgs args |> List.map (transpileExpression env)
        match env.ContextMembers |> Map.tryFind memberKey with
        | Some memberCode -> callClosure memberCode argsCode
        | None ->
            failwith $"Context member {memberName} from {typeExprToKey ctxType} not found in scope. Available: {env.ContextMembers |> Map.toList |> List.map fst}"
    | Pipe(value, funcName, rangeOpt, args) ->
        // Pipe x \f(a, b) is the call f(x, a, b), with everything a call gets (contexts, intrinsics, ...).
        transpileExpression env { expr with Kind = FunctionCall(funcName, rangeOpt, pipeCallArguments value args) }
    | Lambda(args, body) when args.IsEmpty ->
        transpileExpression env body
    | Block statements ->
        let rec transpileStatements env items =
            match items with
            | [] -> failwith "Unsupported block form for WASM MVP backend: block does not end in expression"
            | [ExprStatement expr] -> transpileExpression env expr
            | DefStatement(_, name, _, rhs) :: tail ->
                match env.Locals |> Map.tryFind name with
                | Some localName ->
                    let rhsCode = transpileExpression env rhs
                    // Track record layout if rhs is a RecordExpr
                    // Helper to infer type from literal
                    let inferTypeFromExpr (expr: Expression) =
                        match expr.Kind with
                        | LiteralExpr(StringLit _) -> "string"
                        | LiteralExpr(IntLit _) -> "int"
                        | LiteralExpr(FloatLit _) -> "float"
                        | LiteralExpr(BoolLit _) -> "bool"
                        | _ -> "unknown"

                    let updatedEnv =
                        match rhs.Kind with
                        | RecordExpr fields ->
                            let fieldInfo =
                                fields
                                |> List.mapi (fun i field ->
                                    match field with
                                    | NamedField(n, expr) -> (n, inferTypeFromExpr expr)
                                    | PositionalField expr -> (string i, inferTypeFromExpr expr))
                            let sortedFieldNames = fieldInfo |> List.map fst |> List.sort
                            let fieldTypes = fieldInfo |> Map.ofList
                            { env with
                                RecordLayouts = env.RecordLayouts |> Map.add name sortedFieldNames
                                RecordFieldTypes = env.RecordFieldTypes |> Map.add name fieldTypes
                                ValueTypes = env.ValueTypes |> Map.add name "record" }
                        | TupleExpr items ->
                            let fieldNames = items |> List.mapi (fun i _ -> string i)
                            let fieldTypes = items |> List.mapi (fun i expr -> (string i, inferTypeFromExpr expr)) |> Map.ofList
                            { env with
                                RecordLayouts = env.RecordLayouts |> Map.add name fieldNames
                                RecordFieldTypes = env.RecordFieldTypes |> Map.add name fieldTypes
                                ValueTypes = env.ValueTypes |> Map.add name "tuple" }
                        | FunctionCall("__with", _, Kind (IdentifierExpr baseName) :: _) when env.RecordLayouts |> Map.containsKey baseName ->
                            // A record update has the layout of the record it copies
                            { env with
                                RecordLayouts = env.RecordLayouts |> Map.add name env.RecordLayouts.[baseName]
                                RecordFieldTypes =
                                    match env.RecordFieldTypes |> Map.tryFind baseName with
                                    | Some fieldTypes -> env.RecordFieldTypes |> Map.add name fieldTypes
                                    | None -> env.RecordFieldTypes
                                ValueTypes =
                                    match env.ValueTypes |> Map.tryFind baseName with
                                    | Some tyName -> env.ValueTypes |> Map.add name tyName
                                    | None -> env.ValueTypes }
                        | FunctionCall(typeName, _, _) when env.TypeLayouts |> Map.containsKey typeName ->
                            // Type constructor call - use the type's field types
                            let typeFieldTypes = env.TypeFieldTypes |> Map.tryFind typeName |> Option.defaultValue Map.empty
                            let typeLayout = env.TypeLayouts |> Map.tryFind typeName |> Option.defaultValue []
                            { env with
                                RecordLayouts = env.RecordLayouts |> Map.add name typeLayout
                                RecordFieldTypes = env.RecordFieldTypes |> Map.add name typeFieldTypes
                                ValueTypes = env.ValueTypes |> Map.add name typeName }
                        | _ ->
                            let withSimpleType =
                                match tryGetExprTypeName env rhs with
                                | Some tyName -> { env with ValueTypes = env.ValueTypes |> Map.add name tyName }
                                | None -> env
                            // Carry the full inferred type too, so a record bound here (a captured one read
                            // from a closure's environment, say) keeps its layout.
                            match tryExprTy env rhs with
                            | Some ty when not (env.ValueTys.ContainsKey name) -> { withSimpleType with ValueTys = env.ValueTys |> Map.add name ty }
                            | _ -> withSimpleType
                    let restCode = transpileStatements updatedEnv tail
                    $"{rhsCode}\n    local.set ${localName}\n    {restCode}"
                | None ->
                    failwith $"Unsupported block local for WASM MVP backend: {name}"
            | ExprStatement expr :: tail ->
                let exprCode = transpileExpression env expr
                let restCode = transpileStatements env tail
                $"{exprCode}\n    drop\n    {restCode}"
            | UseStatement (UseValue (Kind (RecordExpr fields))) :: tail ->
                // Closure conversion evaluated each binding into a local; the members are those locals.
                let updatedEnv =
                    fields
                    |> List.fold (fun acc field ->
                        match field with
                        | NamedField(key, Kind (IdentifierExpr local)) when acc.Locals.ContainsKey local ->
                            { acc with ContextMembers = acc.ContextMembers |> Map.add key $"local.get ${acc.Locals.[local]}" }
                        | NamedField(key, _) -> failwith $"The WebAssembly backend cannot compile the `use` binding of '{key}' yet"
                        | PositionalField _ -> failwith "The WebAssembly backend cannot compile a `use` binding without a name") env
                transpileStatements updatedEnv tail
            | UseStatement _ :: _ ->
                failwith "The WebAssembly backend cannot compile this kind of `use` yet"
            | ImportStatement _ :: _
            | TypeDefStatement _ :: _ ->
                failwith "Unsupported statement form for WASM MVP backend"
        transpileStatements env (statements |> List.map _.Kind)
    | _ ->
        let construct =
            match expr.Kind with
            | UseIn _ -> "`use ... in`"
            | ContextMemberAccess _ -> "a context member used as a value"
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> "a workflow keyword here"
            | _ -> "this kind of expression"
        failwith $"The WebAssembly backend cannot compile {construct} yet"

/// The code for `expr`. Whatever the backend cannot compile is reported at the innermost expression it is in that is
/// written in the source.
and private transpileExpression (env: TranspileEnv) (expr: Expression) : string =
    locatedAt expr.Span (fun () -> transpileExpressionUnplaced env expr)

let private defBodyAndParameters (expr: Expression) =
    match expr.Kind with
    | Lambda(args, body) -> args, body
    | _ -> [], expr

/// Lambda lifting. A lambda with parameters that appears as a value (a call or pipe argument, the right-hand
/// side of a local `def`, ...) becomes a top-level function `__lambda_N`, and the expression becomes a reference
/// to that name, so the rest of the backend only ever meets lambdas as definitions. Only a lambda that captures
/// nothing from its enclosing function is lifted; a capturing one is left in place and reported as unsupported
/// when it is lowered (closures are not supported yet).
module private LambdaLifting =
    /// Every name an expression refers to: identifiers, callees and pipe targets.
    let rec private referencedNames (expr: Expression) : string list =
        match expr.Kind with
        | IdentifierExpr name -> [ name ]
        | FunctionCall(name, _, args) -> name :: (args |> List.collect referencedNames)
        | Pipe(value, name, _, args) -> name :: referencedNames value @ (args |> List.collect referencedNames)
        | Lambda(_, body) -> referencedNames body
        | BinaryOp(_, left, right) -> referencedNames left @ referencedNames right
        | IfExpr(cond, thenExpr, elseExpr) -> referencedNames cond @ referencedNames thenExpr @ referencedNames elseExpr
        | Block statements -> statements |> List.collect referencedNamesStatement
        | Match(values, arms) ->
            (values |> List.collect referencedNames)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect referencedNamesPattern) @ referencedNames body))
        | TupleExpr items
        | ListExpr items -> items |> List.collect referencedNames
        | RecordExpr fields ->
            fields |> List.collect (function NamedField(_, value) | PositionalField value -> referencedNames value)
        | TagExpr(_, payload) -> payload |> Option.map referencedNames |> Option.defaultValue []
        | MemberAccess(inner, _, _) -> referencedNames inner
        | UseIn(binding, body) -> referencedNamesBinding binding @ referencedNames body
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> referencedNames value
        | InterpolatedString parts ->
            parts |> List.collect (function StringExpr value -> referencedNames value | StringText _ -> [])
        | ContextMemberCall(_, _, args) -> args |> List.collect referencedNames
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _ -> []

    and private referencedNamesStatement (statement: Statement) : string list =
        match statement.Kind with
        | DefStatement(_, _, _, rhs) -> referencedNames rhs
        | ExprStatement value -> referencedNames value
        | UseStatement binding -> referencedNamesBinding binding
        | ImportStatement _
        | TypeDefStatement _ -> []

    and private referencedNamesBinding (binding: UseBinding) : string list =
        match binding with
        | UseValue value
        | UseContextInstance(_, value) -> referencedNames value

    and private referencedNamesPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | GuardPattern(_, value) -> referencedNames value
        | RangePattern(startValue, endValue) -> referencedNames startValue @ referencedNames endValue
        | TagPattern(_, Some payload) -> referencedNamesPattern payload
        | TuplePattern patterns
        | ListPattern(patterns, _) -> patterns |> List.collect referencedNamesPattern
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencedNamesPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect referencedNamesPattern
        | RecordMemberPattern members -> members |> List.collect (snd >> referencedNamesPattern)
        | TagPattern(_, None)
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | ElsePattern -> []

    /// Every name a pattern binds.
    let rec private boundNamesPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | IdentifierPattern name -> [ name ]
        | TagPattern(_, Some payload) -> boundNamesPattern payload
        | TuplePattern patterns -> patterns |> List.collect boundNamesPattern
        | ListPattern(patterns, rest) ->
            (patterns |> List.collect boundNamesPattern) @ (rest |> Option.map boundNamesPattern |> Option.defaultValue [])
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect boundNamesPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect boundNamesPattern
        | RecordMemberPattern members -> members |> List.collect (fun (name, p) -> name :: boundNamesPattern p)
        | TagPattern(_, None)
        | LiteralPattern _
        | GuardPattern _
        | RangePattern _
        | WildcardPattern
        | ElsePattern -> []

    /// Every name bound anywhere inside an expression: local defs, lambda parameters and pattern variables.
    /// (An over-approximation of what a nested lambda could capture, which errs on the side of not lifting.)
    let rec private boundNames (expr: Expression) : string list =
        match expr.Kind with
        | Lambda(args, body) -> (args |> List.map fst) @ boundNames body
        | Block statements ->
            statements |> List.map _.Kind
            |> List.collect (function
                | DefStatement(_, name, _, rhs) -> name :: boundNames rhs
                | ExprStatement value -> boundNames value
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _ -> [])
        | Match(values, arms) ->
            (values |> List.collect boundNames)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect boundNamesPattern) @ boundNames body))
        | FunctionCall(_, _, args)
        | ContextMemberCall(_, _, args) -> args |> List.collect boundNames
        | Pipe(value, _, _, args) -> boundNames value @ (args |> List.collect boundNames)
        | BinaryOp(_, left, right) -> boundNames left @ boundNames right
        | IfExpr(cond, thenExpr, elseExpr) -> boundNames cond @ boundNames thenExpr @ boundNames elseExpr
        | TupleExpr items
        | ListExpr items -> items |> List.collect boundNames
        | RecordExpr fields -> fields |> List.collect (function NamedField(_, value) | PositionalField value -> boundNames value)
        | TagExpr(_, payload) -> payload |> Option.map boundNames |> Option.defaultValue []
        | MemberAccess(inner, _, _) -> boundNames inner
        | UseIn(_, body) -> boundNames body
        | WorkflowBindExpr(name, value) -> name :: boundNames value
        | WorkflowReturnExpr value -> boundNames value
        | InterpolatedString parts -> parts |> List.collect (function StringExpr value -> boundNames value | StringText _ -> [])
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _
        | IdentifierExpr _ -> []

    /// The name of the environment parameter every lifted function takes first: the closure block itself.
    let envParameter = "__env"

    /// `expr` with every reference to the local function `from` redirected to its lifted form `to'`: a call
    /// becomes a direct call that passes the environment on, and a use as a value is the environment itself
    /// (the closure block).
    let rec private redirectSelfReferences (from: string) (to': string) (expr: Expression) : Expression =
        let recur = redirectSelfReferences from to'
        let rebuild kind = { expr with Kind = kind }
        let flatten (args: Expression list) =
            match args with
            | [ Kind (TupleExpr items) ] -> items
            | _ -> args
        match expr.Kind with
        | IdentifierExpr name when name = from -> rebuild (IdentifierExpr envParameter)
        | IdentifierExpr _ -> expr
        | FunctionCall(name, range, args) when name = from ->
            rebuild (FunctionCall(to', range, [ Expression.from expr (TupleExpr (Expression.from expr (IdentifierExpr envParameter) :: (flatten args |> List.map recur))) ]))
        | FunctionCall(name, range, args) -> rebuild (FunctionCall(name, range, args |> List.map recur))
        | Pipe(value, name, range, args) when name = from ->
            recur (rebuild (FunctionCall(name, range, pipeCallArguments value args)))
        | Pipe(value, name, range, args) -> rebuild (Pipe(recur value, name, range, args |> List.map recur))
        | Lambda(args, body) -> rebuild (Lambda(args, recur body))
        | BinaryOp(op, left, right) -> rebuild (BinaryOp(op, recur left, recur right))
        | IfExpr(cond, thenExpr, elseExpr) -> rebuild (IfExpr(recur cond, recur thenExpr, recur elseExpr))
        | Block statements ->
            statements
            |> List.map (fun statement ->
                match statement.Kind with
                | DefStatement(isExport, name, typeOpt, rhs) -> { statement with Kind = DefStatement(isExport, name, typeOpt, recur rhs) }
                | ExprStatement value -> { statement with Kind = ExprStatement(recur value) }
                | _ -> statement)
            |> Block
            |> rebuild
        | Match(values, arms) -> rebuild (Match(values |> List.map recur, arms |> List.map (fun (patterns, body) -> patterns, recur body)))
        | TupleExpr items -> rebuild (TupleExpr(items |> List.map recur))
        | ListExpr items -> rebuild (ListExpr(items |> List.map recur))
        | RecordExpr fields ->
            fields
            |> List.map (function
                | NamedField(name, value) -> NamedField(name, recur value)
                | PositionalField value -> PositionalField(recur value))
            |> RecordExpr
            |> rebuild
        | TagExpr(name, payload) -> rebuild (TagExpr(name, payload |> Option.map recur))
        | MemberAccess(inner, field, range) -> rebuild (MemberAccess(recur inner, field, range))
        | UseIn(binding, body) -> rebuild (UseIn(binding, recur body))
        | WorkflowBindExpr(name, value) -> rebuild (WorkflowBindExpr(name, recur value))
        | WorkflowReturnExpr value -> rebuild (WorkflowReturnExpr(recur value))
        | InterpolatedString parts ->
            parts |> List.map (function StringExpr value -> StringExpr(recur value) | text -> text) |> InterpolatedString |> rebuild
        | ContextMemberCall(ctx, name, args) -> rebuild (ContextMemberCall(ctx, name, args |> List.map recur))
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _ -> expr

    /// Where a lifted function came from: the top-level def it was written in, its local name there if it
    /// was a `rec` def, and the names of the variables it captures, in the order they sit in its environment.
    /// A lambda lifted from a call's argument also keeps the lambda as written, to find its type in the owner's.
    type LiftedOrigin = { Owner: string; LocalName: string option; Captured: string list; Source: Expression option }

    /// `ty` with the type parameters of a context replaced by the arguments of one of its instances.
    let rec private substituteTypeParameters (arguments: Map<string, TypeExpr>) (ty: TypeExpr) : TypeExpr =
        let recur = substituteTypeParameters arguments
        match ty.Kind with
        | TypeName name
        | TypeVar name when arguments.ContainsKey name -> arguments.[name]
        | TypeFunc(input, output) -> { ty with Kind = TypeFunc(recur input, recur output) }
        | TypeTuple items -> { ty with Kind = TypeTuple(items |> List.map recur) }
        | TypeApply(name, items) -> { ty with Kind = TypeApply(name, items |> List.map recur) }
        | _ -> ty

    /// A lambda bound to a context member, with each parameter it leaves unannotated given the type the member
    /// declares for it, so the lifted function knows its parameters' types (a string to interpolate, say).
    let private annotateFromMemberType (memberType: TypeExpr option) (args: (string * TypeExpr option) list) =
        let declared =
            match memberType with
            | Some (Kind (TypeFunc(Kind (TypeTuple items), _))) when items.Length = args.Length -> items
            | Some (Kind (TypeFunc(input, _))) when args.Length = 1 -> [ input ]
            | _ -> []
        if declared.IsEmpty then args
        else List.map2 (fun (name, annotation) ty -> name, Some (annotation |> Option.defaultValue ty)) args declared

    /// Closure conversion. Every function value is a closure block: a tuple of the function's table slot
    /// followed by the values it captures. A lifted function takes that block as its first parameter,
    /// `__env`, and starts by binding each captured name to the matching field; a call through a value
    /// passes the block as the environment and calls the slot it holds. A top-level function used as a
    /// value gets a wrapper that takes and ignores an environment. Lifts the lambdas of a module into
    /// top-level definitions appended at the end, and returns where each came from (lifting runs after
    /// type checking, so the backend uses that to find the types of parameters and captures) and the
    /// wrapper made for each top-level function used as a value.
    ///
    /// A context member is a function value too. Each binding of a `use` is evaluated into a local of its
    /// own, `__use_<key>_N`, and the statement is left as `use (<key> = __use_<key>_N, ...)`, so a binding
    /// can be any function-valued expression: a lambda (which may capture), a top-level function, a
    /// parameter or local. The top-level functions that can stand in for a context member when nothing in
    /// scope supplies one (the ambient instance, like the runtime's `Console`) get wrappers up front, as do
    /// `synthesized`, the top-level functions an instance the compiler builds from attached definitions is made of.
    let lift (synthesized: Set<string>) (module': Module) : Module * Map<string, LiftedOrigin> * Map<string, string> =
        let lifted = System.Collections.Generic.List<TopLevelItem>()
        let origins = System.Collections.Generic.Dictionary<string, LiftedOrigin>()
        let wrappers = System.Collections.Generic.Dictionary<string, string>()
        let mutable owner = ""
        let mutable counter = 0

        let topLevelFunctions =
            module'
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, Kind (Lambda(args, _))))) when not args.IsEmpty -> Some (name, args.Length)
                | _ -> None)
            |> Map.ofList

        /// The type parameters and member types of every record type, by name: contexts are declared as
        /// records, with or without the `context` modifier.
        let recordTypes =
            module'
            |> List.choose (function
                | Kind (Def (TypeDef(name, _, parameters, Kind (TypeRecord fields)))) ->
                    let members =
                        fields
                        |> List.choose (function
                            | TypeField(fieldName, _, _, typeOpt, _)
                            | TypeMember(fieldName, typeOpt) -> typeOpt |> Option.map (fun ty -> fieldName, ty))
                        |> Map.ofList
                    Some (name, (parameters |> List.map fst, members))
                | _ -> None)
            |> Map.ofList

        /// The type a context declares for a member: of the named context (with its type parameters bound
        /// to the instance's arguments), or of any record type with such a member when the context is not named.
        let memberType (contextType: TypeExpr option) (memberName: string) : TypeExpr option =
            match contextType with
            | Some (Kind (TypeName name)) ->
                recordTypes |> Map.tryFind name |> Option.bind (fun (_, members) -> members |> Map.tryFind memberName)
            | Some (Kind (TypeApply(name, arguments))) ->
                recordTypes
                |> Map.tryFind name
                |> Option.bind (fun (parameters, members) ->
                    members
                    |> Map.tryFind memberName
                    |> Option.map (fun ty ->
                        if parameters.Length = arguments.Length then
                            substituteTypeParameters (List.zip parameters arguments |> Map.ofList) ty
                        else ty))
            | _ -> recordTypes |> Map.toSeq |> Seq.tryPick (fun (_, (_, members)) -> members |> Map.tryFind memberName)

        /// The wrapper that lets the top-level function `name` be called as a function value.
        let wrapperOf (name: string) =
            match wrappers.TryGetValue name with
            | true, existing -> existing
            | _ ->
                let wrapperName = $"__fn_{sanitizeIdentifier name}"
                let parameters = [ for index in 1 .. topLevelFunctions.[name] -> $"__arg{index}" ]
                let call = Expression.synthetic (FunctionCall(name, None, [ Expression.synthetic (TupleExpr (parameters |> List.map (fun p -> Expression.synthetic (IdentifierExpr p)))) ]))
                lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, wrapperName, None, Expression.synthetic (Lambda((envParameter, None) :: (parameters |> List.map (fun p -> p, None)), call))))))
                wrappers.[name] <- wrapperName
                wrapperName

        /// The closure block for the lifted function `liftedName` over the given captured names, standing in for
        /// `origin` (the lambda or function reference it replaces), whose id and span it keeps. (A marker call
        /// rather than a tuple, so that `f(closure)` is not flattened into one argument per field.)
        let closureOf (origin: Expression) (liftedName: string) (captured: string list) =
            { origin with
                Kind =
                    FunctionCall("__closure", None,
                        Expression.synthetic (FunctionCall("__slot", None, [ Expression.synthetic (IdentifierExpr liftedName) ]))
                        :: (captured |> List.map (fun name -> Expression.synthetic (IdentifierExpr name)))) }

        /// The lifted function's lambda: the environment parameter first, then the captured names bound
        /// from the environment's fields ahead of the original body. It is reported where `lambda` was written.
        let liftedLambda (lambda: Expression) (args: (string * TypeExpr option) list) (captured: string list) (body: Expression) =
            let captureBindings =
                captured
                |> List.mapi (fun index name ->
                    Statement.synthetic (DefStatement(false, name, None, Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr envParameter), string (index + 1), None)))))
            let bodyWithCaptures =
                match captureBindings, body.Kind with
                | [], _ -> body
                | _, Block statements -> { body with Kind = Block (captureBindings @ statements) }
                | _, _ -> Expression.from body (Block (captureBindings @ [ Statement.from body (ExprStatement body) ]))
            Expression.from lambda (Lambda((envParameter, None) :: args, bodyWithCaptures))

        // `scope` is every name the enclosing function binds; a lambda referring to one of them captures it.
        let rec liftExpr (scope: Set<string>) (expr: Expression) : Expression =
            let recur = liftExpr scope
            let rebuild kind = { expr with Kind = kind }
            match expr.Kind with
            // A `for` loop's lambda is inlined as the loop body by the backend, never lifted; lift inside it.
            | FunctionCall("__for", range, [ iterable; Kind (Lambda(args, body)) as lambda ]) ->
                let innerScope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList)
                rebuild (FunctionCall("__for", range, [ recur iterable; { lambda with Kind = Lambda(args, liftExpr (Set.union scope innerScope) body) } ]))
            | Lambda(args, body) when not args.IsEmpty -> liftLambda scope expr args body
            | Lambda(args, body) -> rebuild (Lambda(args, recur body))
            // A top-level function used as a value: a closure over a wrapper that takes an environment.
            | IdentifierExpr name when topLevelFunctions.ContainsKey name && not (scope.Contains name) ->
                closureOf expr (wrapperOf name) []
            | FunctionCall(name, range, args) -> rebuild (FunctionCall(name, range, args |> List.map recur))
            | Pipe(value, name, range, args) -> rebuild (Pipe(recur value, name, range, args |> List.map recur))
            | BinaryOp(op, left, right) -> rebuild (BinaryOp(op, recur left, recur right))
            | IfExpr(cond, thenExpr, elseExpr) -> rebuild (IfExpr(recur cond, recur thenExpr, recur elseExpr))
            | Block statements ->
                // The records defined in this block, for `use ctx` where `ctx` is one of them.
                let localRecords =
                    statements
                    |> List.choose (function
                        | Kind (DefStatement(_, name, _, Kind (RecordExpr fields))) -> Some (name, fields)
                        | _ -> None)
                    |> Map.ofList
                let usedRecords =
                    statements
                    |> List.choose (function
                        | Kind (UseStatement(UseValue(Kind (IdentifierExpr name)))) when localRecords.ContainsKey name -> Some name
                        | _ -> None)
                    |> Set.ofList
                // A lambda bound to a context member is a function even with no parameters.
                let liftMember contextType memberName (value: Expression) =
                    match value.Kind with
                    | Lambda(args, body) -> liftLambda scope value (annotateFromMemberType (memberType contextType memberName) args) body
                    | _ -> recur value
                statements
                |> List.collect (fun statement ->
                    let keep kind = { statement with Kind = kind }
                    match statement.Kind with
                    // `rec name = { ... }`: a local function that calls itself. It is lifted under a fresh
                    // name with its self-references redirected there (a self-call passes the environment on),
                    // and the local is bound to its closure.
                    | DefStatement(isExport, name, _, Kind (FunctionCall("__rec", _, [ Kind (Lambda(args, body)) as lambda ]))) when not args.IsEmpty ->
                        let liftedName = $"__rec_{sanitizeIdentifier name}_{counter}"
                        counter <- counter + 1
                        let parameterNames = args |> List.map fst |> Set.ofList
                        let innerScope = Set.union parameterNames (boundNames body |> Set.ofList) |> Set.add name
                        let liftedBody = liftExpr (Set.union scope innerScope) body |> redirectSelfReferences name liftedName
                        let captured =
                            Set.difference (referencedNames liftedBody |> Set.ofList) (innerScope |> Set.add liftedName |> Set.add envParameter)
                            |> Set.intersect scope
                            |> Set.toList
                        lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, liftedName, None, liftedLambda lambda args captured liftedBody))))
                        origins.[liftedName] <- { Owner = owner; LocalName = Some name; Captured = captured; Source = None }
                        [ keep (DefStatement(isExport, name, None, closureOf lambda liftedName captured)) ]
                    | DefStatement(isExport, name, typeOpt, Kind (FunctionCall("__rec", _, [ rhs ]))) ->
                        [ keep (DefStatement(isExport, name, typeOpt, recur rhs)) ]
                    // A record a `use` binds below: its fields are context members.
                    | DefStatement(isExport, name, typeOpt, (Kind (RecordExpr fields) as record)) when usedRecords.Contains name ->
                        let fields =
                            fields
                            |> List.map (function
                                | NamedField(memberName, value) -> NamedField(memberName, liftMember None memberName value)
                                | PositionalField value -> PositionalField(recur value))
                        [ keep (DefStatement(isExport, name, typeOpt, { record with Kind = RecordExpr fields })) ]
                    | DefStatement(isExport, name, typeOpt, rhs) -> [ keep (DefStatement(isExport, name, typeOpt, recur rhs)) ]
                    | ExprStatement value -> [ keep (ExprStatement(recur value)) ]
                    | UseStatement binding ->
                        let namedFields (fields: RecordField list) =
                            fields |> List.choose (function NamedField(name, value) -> Some (name, value) | PositionalField _ -> None)
                        let contextType, bindings =
                            match binding with
                            | UseValue(Kind (RecordExpr fields)) -> None, namedFields fields
                            | UseValue(Kind (FunctionCall(contextName, _, [ Kind (RecordExpr fields) ]))) -> Some (TypeExpr.synthetic (TypeName contextName)), namedFields fields
                            | UseContextInstance(contextType, Kind (RecordExpr fields)) -> Some contextType, namedFields fields
                            | UseValue(Kind (IdentifierExpr recordName) as recordRef) when localRecords.ContainsKey recordName ->
                                None,
                                namedFields localRecords.[recordName]
                                |> List.map (fun (name, _) -> name, Expression.from recordRef (MemberAccess(Expression.from recordRef (IdentifierExpr recordName), name, None)))
                            | _ -> None, []
                        if bindings.IsEmpty then [ statement ]
                        else
                            let locals =
                                bindings
                                |> List.map (fun (memberName, value) ->
                                    let key = contextMemberKey contextType memberName
                                    let local = $"__use_{sanitizeIdentifier key}_{counter}"
                                    counter <- counter + 1
                                    key, local, liftMember contextType memberName value)
                            [ yield! locals |> List.map (fun (_, local, value) -> Statement.from value (DefStatement(false, local, None, value)))
                              keep (UseStatement(UseValue(Expression.synthetic (RecordExpr(locals |> List.map (fun (key, local, _) -> NamedField(key, Expression.synthetic (IdentifierExpr local)))))))) ]
                    | ImportStatement _ | TypeDefStatement _ -> [ statement ])
                |> Block
                |> rebuild
            | Match(values, arms) -> rebuild (Match(values |> List.map recur, arms |> List.map (fun (patterns, body) -> patterns, recur body)))
            | TupleExpr items -> rebuild (TupleExpr(items |> List.map recur))
            | ListExpr items -> rebuild (ListExpr(items |> List.map recur))
            | RecordExpr fields ->
                fields
                |> List.map (function
                    | NamedField(name, value) -> NamedField(name, recur value)
                    | PositionalField value -> PositionalField(recur value))
                |> RecordExpr
                |> rebuild
            | TagExpr(name, payload) -> rebuild (TagExpr(name, payload |> Option.map recur))
            | MemberAccess(inner, field, range) -> rebuild (MemberAccess(recur inner, field, range))
            | UseIn(binding, body) -> rebuild (UseIn(binding, recur body))
            | WorkflowBindExpr(name, value) -> rebuild (WorkflowBindExpr(name, recur value))
            | WorkflowReturnExpr value -> rebuild (WorkflowReturnExpr(recur value))
            | InterpolatedString parts ->
                parts
                |> List.map (function
                    | StringExpr value -> StringExpr(recur value)
                    | text -> text)
                |> InterpolatedString
                |> rebuild
            | ContextMemberCall(ctx, name, args) -> rebuild (ContextMemberCall(ctx, name, args |> List.map recur))
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> expr

        /// Lifts `{ args -> body }` to a fresh top-level function and returns its closure.
        and liftLambda (scope: Set<string>) (lambda: Expression) (args: (string * TypeExpr option) list) (body: Expression) : Expression =
            let parameterNames = args |> List.map fst |> Set.ofList
            let innerScope = Set.union parameterNames (boundNames body |> Set.ofList)
            // A lambda nested inside may capture what this one binds and what encloses this one alike;
            // what it captures from outside is then referenced here, so this lambda captures it in turn.
            let liftedBody = liftExpr (Set.union scope innerScope) body
            let captured =
                Set.difference (referencedNames liftedBody |> Set.ofList) innerScope
                |> Set.intersect scope
                |> Set.toList
            let name = $"__lambda_{counter}"
            counter <- counter + 1
            lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, name, None, liftedLambda lambda args captured liftedBody))))
            origins.[name] <- { Owner = owner; LocalName = None; Captured = captured; Source = Some lambda }
            closureOf lambda name captured

        let liftItem (item: TopLevelItem) : TopLevelItem =
            match item.Kind with
            // A definition's own lambda is the function being defined; lift inside its body only.
            | Def (ValueDef(isExport, name, typeOpt, (Kind (Lambda(args, body)) as lambda))) ->
                owner <- name
                let scope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList)
                { item with Kind = Def (ValueDef(isExport, name, typeOpt, { lambda with Kind = Lambda(args, liftExpr scope body) })) }
            | Def (ValueDef(isExport, name, typeOpt, rhs)) ->
                owner <- name
                { item with Kind = Def (ValueDef(isExport, name, typeOpt, liftExpr (boundNames rhs |> Set.ofList) rhs)) }
            | Expr expr ->
                owner <- ""
                { item with Kind = Expr (liftExpr (boundNames expr |> Set.ofList) expr) }
            | _ -> item

        let items = module' |> List.map liftItem

        // The ambient instance: a top-level function named like a member of a declared context, which takes
        // no contexts itself, stands in for that member wherever nothing else supplies it. Only worth a
        // wrapper when some function takes contexts.
        let takesContexts (typeOpt: TypeExpr option) =
            match typeOpt with
            | Some (Kind (TypeWithContext _))
            | Some (Kind (TypeContext _)) -> true
            | _ -> false
        let defTypes =
            module' |> List.choose (function Kind (Def (ValueDef(_, name, typeOpt, _))) -> Some (name, typeOpt) | _ -> None) |> Map.ofList
        if defTypes |> Map.exists (fun _ typeOpt -> takesContexts typeOpt) then
            module'
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, _, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
                    fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
            |> List.distinct
            |> List.filter (fun name -> topLevelFunctions.ContainsKey name && not (takesContexts defTypes.[name]))
            |> List.iter (wrapperOf >> ignore)
        synthesized |> Set.filter topLevelFunctions.ContainsKey |> Set.iter (wrapperOf >> ignore)

        items @ List.ofSeq lifted,
        origins |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq,
        wrappers |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq

let private extractTypeLayouts (module': Module) : Map<string, string list> * Map<string, Map<string, string>> =
    let rec typeExprToTypeString (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name -> name
        | TypeFunc(param, ret) -> $"({typeExprToTypeString param}) -> ({typeExprToTypeString ret})"
        | TypeUnit -> "()"
        | TypeTuple items -> items |> List.map typeExprToTypeString |> String.concat ", " |> sprintf "(%s)"
        | _ -> "unknown"
    let results =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, Kind (TypeRecord fields)))) ->
                let fieldInfo =
                    fields
                    |> List.choose (function
                        | TypeField(fieldName, _, _, typeOpt, _) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName)
                        | TypeMember(fieldName, typeOpt) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName))
                let sortedFieldNames = fieldInfo |> List.map fst |> List.sort
                let fieldTypeMap = fieldInfo |> Map.ofList
                Some (name, sortedFieldNames, fieldTypeMap)
            | _ -> None)
    let layouts = results |> List.map (fun (n, fields, _) -> (n, fields)) |> Map.ofList
    let fieldTypes = results |> List.map (fun (n, _, types) -> (n, types)) |> Map.ofList
    layouts, fieldTypes

let private getLayoutFromTypeExpr (typeLayouts: Map<string, string list>) (typeFieldTypes: Map<string, Map<string, string>>) (typeExpr: TypeExpr option) : (string list * Map<string, string>) option =
    match typeExpr with
    | Some (Kind (TypeName typeName)) ->
        match typeLayouts |> Map.tryFind typeName, typeFieldTypes |> Map.tryFind typeName with
        | Some layout, Some types -> Some (layout, types)
        | Some layout, None -> Some (layout, Map.empty)
        | _ -> None
    | Some (Kind (TypeRecord fields)) ->
        let fieldInfo =
            fields
            |> List.choose (function
                | TypeField(fieldName, _, _, typeOpt, _) ->
                    let typeName =
                        match typeOpt with
                        | Some (Kind (TypeName t)) -> t
                        | _ -> "unknown"
                    Some (fieldName, typeName)
                | TypeMember(fieldName, typeOpt) ->
                    let typeName =
                        match typeOpt with
                        | Some (Kind (TypeName t)) -> t
                        | _ -> "unknown"
                    Some (fieldName, typeName))
        let fieldNames = fieldInfo |> List.map fst |> List.sort
        let fieldTypes = fieldInfo |> Map.ofList
        Some (fieldNames, fieldTypes)
    | _ -> None

let private transpileModuleToWatCore (module': Module) (typedFunctionBodies: Map<string, TypedExpr>) (typedValueTypes: Map<string, string>) (typedReturnTypes: Map<string, string>) (typedTopLevelTys: Map<string, Ty>) (contextInstances: Map<NodeId, ContextInstance option list>) : string =
    // The definitions synthesized instances are made of are passed as function values.
    let synthesizedMembers =
        contextInstances
        |> Map.values
        |> Seq.collect (List.choose (function Some (InstanceSynthesized members) -> Some members | _ -> None) >> List.concat)
        |> Seq.map snd
        |> Set.ofSeq
    let module', liftedOrigins, functionWrappers = LambdaLifting.lift synthesizedMembers module'
    let defs =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, typeOpt, expr))) -> Some(name, typeOpt, expr)
            | _ -> None)

    let topLevelExprs =
        module'
        |> List.choose (function
            | Kind (Expr expr) -> Some expr
            | _ -> None)

    let typeLayouts, typeFieldTypes = extractTypeLayouts module'

    let nominalWrappers =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, body))) when isNominalTypeName name && (match body.Kind with TypeRecord _ -> false | _ -> true) -> Some name
            | _ -> None)
        |> Set.ofList

    let knownFunctions = defs |> List.map (fun (n, _, _) -> n) |> Set.ofList

    let functionArities =
        defs
        |> List.map (fun (name, _, expr) ->
            let fnParams, _ = defBodyAndParameters expr
            name, fnParams.Length)
        |> Map.ofList

    let functionParameters =
        defs
        |> List.map (fun (name, _, expr) ->
            let fnParams, _ = defBodyAndParameters expr
            name, fnParams |> List.map fst)
        |> Map.ofList

    // Calculate context requirements for each function based on TypeWithContext annotations
    // For parameterized contexts like ToString(a), generate qualified keys like "ToString_a_toString"
    // For simple contexts like Println, keep simple member names like "println"
    let functionContextTypes =
        defs
        |> List.choose (fun (name, typeOpt, _) ->
            match extractContextTypes typeOpt with
            | [] -> None
            | contextTypes -> Some (name, contextTypes))
        |> Map.ofList

    let functionContextRequirements =
        defs
        |> List.choose (fun (name, typeOpt, _) ->
            let contextTypes = extractContextTypes typeOpt
            if contextTypes.IsEmpty then None
            else
                // For each context type, get its field names and generate appropriate keys
                let contextFnNames =
                    contextTypes
                    |> List.collect (fun ctxType ->
                        let baseName = match ctxType.Kind with
                                       | TypeName n -> n
                                       | TypeApply(n, _) -> n
                                       | _ -> "unknown"
                        let needsQualified = match ctxType.Kind with
                                             | TypeApply _ -> true  // Parameterized - need qualified
                                             | _ -> false           // Simple - keep simple names
                        typeLayouts |> Map.tryFind baseName 
                        |> Option.defaultValue []
                        |> List.map (fun memberName ->
                            if needsQualified then
                                let ctxKey = typeExprToKey ctxType
                                $"{ctxKey}_{memberName}"
                            else
                                memberName))
                if contextFnNames.IsEmpty then None
                else Some (name, contextFnNames))
        |> Map.ofList

    // The ambient context instance: a module-level function named like a member of a declared context stands
    // in for that member whenever a callee requires the context and the caller has nothing else to supply.
    // This is how the runtime-provided `Console` (the functions `nanyx/console` exports) reaches `@entry`.
    // Closure conversion made a wrapper for each such function, so the member is a closure over it.
    let ambientContextMembers =
        let contextMemberNames =
            module'
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, _, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
                    fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
        if functionContextRequirements.IsEmpty then Map.empty
        else
            contextMemberNames
            |> List.filter (fun memberName -> not (functionContextRequirements.ContainsKey memberName))
            |> List.choose (fun memberName -> functionWrappers |> Map.tryFind memberName |> Option.map (fun wrapper -> memberName, wrapper))
            |> Map.ofList

    let functionTable: FunctionTable =
        { Values = System.Collections.Generic.Dictionary<string, ContextFn>()
          NextSlot = 0
          IndirectArities = System.Collections.Generic.HashSet<int>() }

    let recordTable: RecordTable =
        { LayoutIds = System.Collections.Generic.Dictionary<string list, int>()
          OpenFieldIds = System.Collections.Generic.Dictionary<string, int>() }

    // The result types the backend can read off definitions' annotations, plus what inference told it.
    let functionReturnTypes =
        let fromAnnotations =
            defs
            |> List.choose (fun (name, typeOpt, _) ->
                match unwrapContextType typeOpt with
                | Some (Kind (TypeFunc(_, Kind (TypeName "string")))) -> Some (name, "string")
                | Some (Kind (TypeFunc(_, Kind (TypeName "int")))) -> Some (name, "int")
                | Some (Kind (TypeFunc(_, Kind (TypeName "bool")))) -> Some (name, "bool")
                | Some (Kind (TypeFunc(_, Kind (TypeApply("list", _))))) -> Some (name, "list")
                | _ -> None)
            |> Map.ofList
        let fromIntrinsics =
            [ Helpers.stringLength, "int"
              Helpers.readStdinByte, "int"
              Helpers.stringByteAt, "int"
              Helpers.stringSlice, "string"
              Helpers.stringFromByte, "string" ]
            |> Map.ofList
        typedReturnTypes
        |> Map.fold (fun acc key value -> acc |> Map.add key value) fromAnnotations
        |> Map.fold (fun acc key value -> acc |> Map.add key value) fromIntrinsics

    let usedHelpers = System.Collections.Generic.HashSet<string>()

    let moduleUsesDbg =
        (defs |> List.exists (fun (_, _, expr) -> containsDbgCallExpr expr)) ||
        (topLevelExprs |> List.exists containsDbgCallExpr)
    let moduleUsesRegularDbg =
        (defs |> List.exists (fun (_, _, expr) -> containsRegularDbgExpr expr)) ||
        (topLevelExprs |> List.exists containsRegularDbgExpr)
    let moduleUsesDbgString = 
        (defs |> List.exists (fun (_, _, expr) -> containsDbgStringLiteralExpr expr)) ||
        (topLevelExprs |> List.exists containsDbgStringLiteralExpr)
    let moduleUsesHeap = 
        (defs |> List.exists (fun (_, _, expr) -> usesHeapAllocationExpr expr)) ||
        (topLevelExprs |> List.exists usesHeapAllocationExpr)
    let dbgTagNames =
        (defs |> List.collect (fun (_, _, expr) -> collectDbgTagNamesExpr expr)) @
         (topLevelExprs |> List.collect collectDbgTagNamesExpr)
        |> List.distinct
        |> List.sort
    let dbgTagPayloadNames =
        (defs |> List.collect (fun (_, _, expr) -> collectDbgTagPayloadNamesExpr expr)) @
         (topLevelExprs |> List.collect collectDbgTagPayloadNamesExpr)
        |> List.distinct
        |> List.sort
    let tagIdMap =
        (defs |> List.collect (fun (_, _, expr) -> collectTagNamesExpr expr)) @
         (topLevelExprs |> List.collect collectTagNamesExpr)
        |> List.distinct
        |> List.sort
        |> List.mapi (fun index name -> name, index + 1)
        |> Map.ofList

    let stringLiterals =
        (defs |> List.collect (fun (_, _, expr) -> collectStringLiteralsExpr expr)) @
         (topLevelExprs |> List.collect collectStringLiteralsExpr)
        |> List.distinct

    let stringLiteralMap, stringLiteralData = buildStringLiteralMap stringLiterals

    // What fails outside any expression of a function is reported at the function's definition.
    let rec renderFunction (name: string) (defTypeOpt: TypeExpr option) (expr: Expression) (typedExprOpt: TypedExpr option) (exportFn: bool) =
        locatedAt expr.Span (fun () -> renderFunctionUnplaced name defTypeOpt expr typedExprOpt exportFn)

    and renderFunctionUnplaced (name: string) (defTypeOpt: TypeExpr option) (expr: Expression) (typedExprOpt: TypedExpr option) (exportFn: bool) =
            let parameters, bodyExpr = defBodyAndParameters expr
            let parameterNames = parameters |> List.map fst
            let parameterMap = createNameMap parameterNames
            let localNames =
                match bodyExpr.Kind with
                | Block statements ->
                    collectBlockLocalNames statements
                    |> List.filter (fun n -> not (parameterMap |> Map.containsKey n))
                | _ -> []
            let localMap =
                localNames
                |> List.fold (fun acc name ->
                    if acc |> Map.containsKey name then acc
                    else
                        let baseName = sanitizeIdentifier name
                        let mutable candidate = baseName
                        let mutable index = 1
                        while (parameterMap |> Map.exists (fun _ v -> v = candidate)) || (acc |> Map.exists (fun _ v -> v = candidate)) do
                            candidate <- $"{baseName}_{index}"
                            index <- index + 1
                        acc |> Map.add name candidate) Map.empty
            let usesDbg = containsDbgCallExpr bodyExpr
            let dbgLocalName =
                if usesDbg then
                    let baseName = "__dbg_tmp"
                    let mutable candidate = baseName
                    let mutable index = 1
                    while (parameterMap |> Map.exists (fun _ v -> v = candidate)) || (localMap |> Map.exists (fun _ v -> v = candidate)) do
                        candidate <- $"{baseName}_{index}"
                        index <- index + 1
                    Some candidate
                else
                    None
            let locals = parameterMap |> Map.fold (fun acc k v -> acc |> Map.add k v) localMap
            let mutable usedLocalNames =
                locals
                |> Map.toList
                |> List.map snd
                |> Set.ofList

            let interpolationLocals = interpolationLocalsFor bodyExpr

            for localName in interpolationLocals do
                usedLocalNames <- usedLocalNames |> Set.add localName

            let matchArity = countMatchArity bodyExpr
            let matchTempNames =
                [ for index in 0 .. (matchArity - 1) do
                    let baseName = $"__match_tmp_{index}"
                    let mutable candidate = baseName
                    let mutable suffix = 1
                    while usedLocalNames |> Set.contains candidate do
                        candidate <- $"{baseName}_{suffix}"
                        suffix <- suffix + 1
                    usedLocalNames <- usedLocalNames |> Set.add candidate
                    yield candidate ]

            // Initialize RecordLayouts from def-level type annotations
            // Extract parameter types from TypeFunc chain: (A -> B -> C) means params are A, B
            let rec extractParamTypes (t: TypeExpr) : TypeExpr list =
                match t.Kind with
                | TypeFunc(param, ret) -> param :: extractParamTypes ret
                | _ -> []

            // `(a, b) -> r` annotates a two-parameter function with one tuple type: split it over the parameters.
            // The annotation can describe more inputs than the lambda declares: `() -> r` has a unit input but
            // no parameters, and `a -> (b -> c)` is a one-parameter function that returns a function. Only keep
            // as many types as there are parameters, so the padding below never needs a negative count.
            let defParamTypes =
                let annotated =
                    match unwrapContextType defTypeOpt with
                    | Some (Kind (TypeFunc(Kind (TypeTuple items), _))) when items.Length = parameterNames.Length -> items
                    | Some (Kind (TypeFunc(Kind TypeUnit, _))) when parameterNames.IsEmpty -> []
                    | Some t -> extractParamTypes t
                    | None -> []
                annotated |> List.truncate parameterNames.Length

            let initialRecordInfo =
                List.zip parameterNames (defParamTypes @ List.replicate (parameterNames.Length - defParamTypes.Length) (TypeExpr.synthetic TypeUnit))
                |> List.choose (fun (paramName, paramType) ->
                    match getLayoutFromTypeExpr typeLayouts typeFieldTypes (Some paramType) with
                    | Some (fieldNames, fieldTypes) -> Some (paramName, fieldNames, fieldTypes)
                    | None ->
                        // Also try lambda parameter type annotation
                        match parameters |> List.tryFind (fun (n, _) -> n = paramName) with
                        | Some (_, paramTypeOpt) ->
                            getLayoutFromTypeExpr typeLayouts typeFieldTypes paramTypeOpt
                            |> Option.map (fun (fieldNames, fieldTypes) -> (paramName, fieldNames, fieldTypes))
                        | None -> None)

            let initialRecordLayouts =
                initialRecordInfo
                |> List.map (fun (n, fields, _) -> (n, fields))
                |> Map.ofList

            let initialRecordFieldTypes =
                initialRecordInfo
                |> List.map (fun (n, _, types) -> (n, types))
                |> Map.ofList

            // Build param types map for string detection during dbg calls
            let rec typeExprToString (t: TypeExpr) =
                match t.Kind with
                | TypeName "string" -> Some "string"
                | TypeName "int" -> Some "int"
                | TypeName "bool" -> Some "bool"
                | TypeApply("list", _) -> Some "list"
                | TypeTuple _ -> Some "tuple"
                | TypeRecord _ -> Some "record"
                | TypeFunc _ -> Some "func"
                | _ -> None
            // A parameter the def's annotation doesn't cover may carry its own (a lambda bound to a context
            // member is annotated with the member's parameter types when it is lifted).
            let initialParamTypes =
                parameters
                |> List.mapi (fun index (paramName, annotation) ->
                    List.tryItem index defParamTypes
                    |> Option.orElse annotation
                    |> Option.bind typeExprToString
                    |> Option.map (fun t -> paramName, t))
                |> List.choose id
                |> Map.ofList

            let typedLocalValueTypes =
                typedExprOpt
                |> Option.map collectTypedValueTypes
                |> Option.defaultValue Map.empty

            let valueTys =
                typedExprOpt
                |> Option.map collectTypedValueTys
                |> Option.defaultValue Map.empty
                |> Map.fold (fun acc key value -> acc |> Map.add key value) typedTopLevelTys

            let initialValueTypes =
                typedValueTypes
                |> Map.fold (fun acc key value -> acc |> Map.add key value) Map.empty
                |> Map.fold (fun acc key value -> acc |> Map.add key value) initialParamTypes
                |> Map.fold (fun acc key value -> acc |> Map.add key value) typedLocalValueTypes

            // Check if this function requires contexts (from TypeWithContext annotation)
            let requiredContextFns = functionContextRequirements |> Map.tryFind name |> Option.defaultValue []
            let contextParamNames =
                requiredContextFns
                |> List.map (fun ctxFnName -> (ctxFnName, $"__ctx_{sanitizeIdentifier ctxFnName}"))

            // Each member arrives as a closure block in a parameter of its own. A member of a parameterised
            // context (`Show_a_show`) can also be called by its own name (`show`), as the type checker allows, when
            // no other context the function declares has a member of that name.
            let contextMembersFromParams =
                let qualified = contextParamNames |> List.map (fun (ctxFnName, paramName) -> ctxFnName, $"local.get ${paramName}")
                let byMemberName =
                    functionContextTypes
                    |> Map.tryFind name
                    |> Option.defaultValue []
                    |> List.collect (fun ctxType ->
                        match ctxType.Kind with
                        | TypeApply(baseName, _) ->
                            typeLayouts |> Map.tryFind baseName |> Option.defaultValue []
                            |> List.map (fun memberName -> memberName, contextMemberKey (Some ctxType) memberName)
                        | _ -> [])
                    |> List.groupBy fst
                    |> List.choose (fun (memberName, keys) ->
                        match keys, qualified |> List.tryFind (fun (key, _) -> key = snd keys.Head) with
                        | [ _ ], Some (_, code) when not (qualified |> List.exists (fun (key, _) -> key = memberName)) -> Some (memberName, code)
                        | _ -> None)
                qualified @ byMemberName |> Map.ofList

            let env =
                { KnownFunctions = knownFunctions
                  FunctionArities = functionArities
                  FunctionParameters = functionParameters
                  FunctionContextRequirements = functionContextRequirements
                  FunctionReturnTypes = functionReturnTypes
                  Locals = locals
                  DbgTempLocal = dbgLocalName
                  TagIds = tagIdMap
                  MatchTempNames = matchTempNames
                  NextMatchTemp = 0
                  StringLiterals = stringLiteralMap
                  ContextMembers = contextMembersFromParams
                  AmbientContextMembers = ambientContextMembers
                  RecordLayouts = initialRecordLayouts
                  TypeLayouts = typeLayouts
                  NominalWrappers = nominalWrappers
                  ContextInstances = contextInstances
                  FunctionContextTypes = functionContextTypes
                  FunctionWrappers = functionWrappers
                  TypeFieldTypes = typeFieldTypes
                  RecordFieldTypes = initialRecordFieldTypes
                  ParamTypes = initialParamTypes
                  ValueTypes = initialValueTypes
                  ValueTys = valueTys
                  UsedHelpers = usedHelpers
                  FunctionTable = functionTable
                  RecordTable = recordTable
                  ForDepth = 0
                  TailCalls = TailPosition.callsIn true bodyExpr }
            let fn = sanitizeIdentifier name
            
            // Build parameter signature including context params
            let regularParamSig =
                parameters
                |> List.map (fun (paramName, _) -> $"(param ${parameterMap.[paramName]} i32)")
                |> String.concat " "
            let contextParamSig =
                contextParamNames
                |> List.map (fun (_, paramName) -> $"(param ${paramName} i32)")
                |> String.concat " "
            let parameterSig =
                [regularParamSig; contextParamSig]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " "
            let payloadLocals = collectPayloadLocalsExpr bodyExpr |> List.distinct
            let payloadLocalNames = payloadLocals |> List.map (fun ident -> $"__payload_{ident}")
            let localsSig =
                [
                    yield! (localNames |> List.map (fun localName -> localMap.[localName]))
                    yield! matchTempNames
                    yield! payloadLocalNames
                    yield! interpolationLocals
                    yield! collectForLoopLocals 0 bodyExpr
                    match dbgLocalName with
                    | Some name -> yield name
                    | None -> ()
                ]
                |> List.distinct
                |> List.map (fun localName -> $"(local ${localName} i32)")
                |> String.concat " "
            let functionHeader =
                [ if not (String.IsNullOrWhiteSpace parameterSig) then parameterSig
                  "(result i32)"
                  if not (String.IsNullOrWhiteSpace localsSig) then localsSig ]
                |> String.concat " "
            let bodyCode = transpileExpression env bodyExpr
            if exportFn then
                $"  (func ${fn} {functionHeader}\n    {bodyCode}\n  )\n  (export \"{name}\" (func ${fn}))"
            else
                $"  (func ${fn} {functionHeader}\n    {bodyCode}\n  )"

    let renderedUserFunctions =
        defs
        |> List.map (fun (name, typeOpt, expr) ->
            let typedExprOpt =
                typedFunctionBodies
                |> Map.tryFind name
                |> Option.orElseWith (fun () ->
                    // A lifted function has no typed body of its own. Its environment's type is a tuple of
                    // the captured names' types in the def it came from, and a `rec` function's parameter
                    // types are those of the local it was there.
                    liftedOrigins
                    |> Map.tryFind name
                    |> Option.bind (fun origin ->
                        typedFunctionBodies
                        |> Map.tryFind origin.Owner
                        |> Option.map (fun ownerTyped ->
                            let ownerTys = collectTypedValueTys ownerTyped
                            let envTy =
                                origin.Captured
                                |> List.mapi (fun index captured -> string (index + 1), ownerTys |> Map.tryFind captured)
                                |> List.choose (fun (key, tyOpt) -> tyOpt |> Option.map (fun ty -> key, ty))
                                |> Map.ofList
                                |> Map.add "0" (TyPrimitive "int")
                                |> TyRecord
                            // A lambda passed to a call is found as written among the owner's typed call arguments.
                            let sourceTyped = origin.Source |> Option.bind (fun source -> tryFindTypedExpr source ownerTyped)
                            let localTy =
                                match origin.LocalName with
                                | Some localName -> ownerTys |> Map.tryFind localName |> Option.map Unifier.unalias
                                | None -> sourceTyped |> Option.map (fun typed -> Unifier.unalias typed.Type)
                            let recordInput ty =
                                match Unifier.unalias ty with
                                | TyRecord fields -> Some fields
                                | _ -> None
                            let parameterTys =
                                match localTy, origin.Source with
                                | Some (TyFunc(single, _)), Some (Kind (Lambda([ _ ], _))) -> [ "1", single ]
                                | Some (TyFunc(input, _)), _ when (recordInput input).IsSome ->
                                    (recordInput input).Value
                                    |> Map.toList
                                    |> List.choose (fun (key, ty) ->
                                        match Int32.TryParse key with
                                        | true, index -> Some (string (index + 1), ty)
                                        | _ -> None)
                                | Some (TyFunc(single, _)), _ -> [ "1", single ]
                                | _ -> []
                            let returnTy =
                                match localTy with
                                | Some (TyFunc(_, ret)) -> ret
                                | _ -> TyVar { Id = -1; Name = None }
                            let inputTy = TyRecord (Map.ofList (("0", envTy) :: parameterTys))
                            { Expr = expr; Type = TyFunc(inputTy, returnTy); Body = sourceTyped |> Option.bind (fun typed -> typed.Body)
                              Statements = None; MatchArms = None; Arguments = None })))
            renderFunction name typeOpt expr typedExprOpt true)

    let renderedFunctions = renderedUserFunctions |> String.concat "\n\n"

    let renderedStartFunction, startDirective =
        if topLevelExprs.IsEmpty then
            "", ""
        else
            let localNames =
                topLevelExprs
                |> List.collect collectExprLocalNames
                |> List.distinct
            let localMap = createNameMap localNames
            let dbgLocalName =
                if topLevelExprs |> List.exists containsDbgCallExpr then Some "__dbg_tmp"
                else None
            let mutable usedLocalNames =
                ([ yield! localMap |> Map.toList |> List.map snd
                   match dbgLocalName with
                   | Some name -> yield name
                   | None -> () ]
                 |> Set.ofList)
            let matchArity = topLevelExprs |> List.map countMatchArity |> maxOrZero
            let matchTempNames =
                [ for index in 0 .. (matchArity - 1) do
                    let baseName = $"__match_tmp_{index}"
                    let mutable candidate = baseName
                    let mutable suffix = 1
                    while usedLocalNames |> Set.contains candidate do
                        candidate <- $"{baseName}_{suffix}"
                        suffix <- suffix + 1
                    usedLocalNames <- usedLocalNames |> Set.add candidate
                    yield candidate ]
            let payloadLocalNames =
                topLevelExprs
                |> List.collect collectPayloadLocalsExpr
                |> List.distinct
                |> List.map (fun ident -> $"__payload_{ident}")
            let localsSig =
                [ yield! (localNames |> List.map (fun localName -> localMap.[localName]))
                  yield! matchTempNames
                  yield! payloadLocalNames
                  yield! interpolationLocalsFor (Expression.synthetic (Block (topLevelExprs |> List.map (fun expr -> Statement.from expr (ExprStatement expr)))))
                  yield! (topLevelExprs |> List.collect (collectForLoopLocals 0))
                  match dbgLocalName with
                  | Some name -> yield name
                  | None -> () ]
                |> List.distinct
                |> List.map (fun localName -> $"(local ${localName} i32)")
                |> String.concat " "
            let env =
                { KnownFunctions = knownFunctions
                  FunctionArities = functionArities
                  FunctionParameters = functionParameters
                  FunctionContextRequirements = functionContextRequirements
                  FunctionReturnTypes = functionReturnTypes
                  Locals = localMap
                  DbgTempLocal = dbgLocalName
                  TagIds = tagIdMap
                  MatchTempNames = matchTempNames
                  NextMatchTemp = 0
                  StringLiterals = stringLiteralMap
                  ContextMembers = Map.empty
                  AmbientContextMembers = ambientContextMembers
                  RecordLayouts = Map.empty
                  TypeLayouts = typeLayouts
                  NominalWrappers = nominalWrappers
                  ContextInstances = contextInstances
                  FunctionContextTypes = functionContextTypes
                  FunctionWrappers = functionWrappers
                  TypeFieldTypes = typeFieldTypes
                  RecordFieldTypes = Map.empty
                  ParamTypes = Map.empty
                  ValueTypes = typedValueTypes
                  ValueTys = typedTopLevelTys
                  UsedHelpers = usedHelpers
                  FunctionTable = functionTable
                  RecordTable = recordTable
                  ForDepth = 0
                  // The start function returns nothing, so nothing in it is a tail call.
                  TailCalls = Set.empty }
            let bodyCode =
                topLevelExprs
                |> List.map (fun expr -> $"{transpileExpression env expr}\n    drop")
                |> String.concat "\n    "
            let header =
                if String.IsNullOrWhiteSpace localsSig then ""
                else " " + localsSig
            $"  (func $__nanyx_start{header}\n    {bodyCode}\n  )", "  (start $__nanyx_start)"

    let moduleUsesDbgHelpers = moduleUsesRegularDbg || moduleUsesDbgString

    // The runtime helpers the lowered code asked for, closed over what they call themselves.
    let helperDependencies =
        [ Helpers.alloc, []
          Helpers.copyBytes, []
          Helpers.stringLength, []
          Helpers.stringAlloc, [ Helpers.alloc ]
          Helpers.stringByteAt, [ Helpers.stringLength ]
          Helpers.stringSlice, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
          Helpers.stringFromByte, [ Helpers.stringAlloc ]
          Helpers.stringConcat, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
          Helpers.stringEquals, [ Helpers.stringLength ]
          Helpers.writeStdout, [ Helpers.stringLength ]
          Helpers.writeStderr, [ Helpers.stringLength ]
          Helpers.readStdinByte, []
          Helpers.listCons, [ Helpers.alloc ]
          Helpers.recordSet, []
          Helpers.recordCopy, [ Helpers.alloc; Helpers.copyBytes ]
          Helpers.tagBox, [ Helpers.alloc ]
          Helpers.tagCode, []
          Helpers.tagEquals, []
          Helpers.floatBox, [ Helpers.alloc ]
          Helpers.floatToString, [ Helpers.alloc ]
          Helpers.listLength, []
          Helpers.listConcat, [ Helpers.listCons ] ]
    let rec closeHelpers (acc: Set<string>) (pending: string list) =
        match pending with
        | [] -> acc
        | name :: rest when acc.Contains name -> closeHelpers acc rest
        | name :: rest ->
            let deps =
                helperDependencies
                |> List.tryFind (fun (helper, _) -> helper = name)
                |> Option.map snd
                |> Option.defaultValue []
            closeHelpers (acc.Add name) (deps @ rest)
    let usedHelperSet = closeHelpers Set.empty (usedHelpers |> Seq.toList)
    let moduleUsesStandardStreams =
        [ Helpers.writeStdout; Helpers.writeStderr; Helpers.readStdinByte ] |> List.exists usedHelperSet.Contains
    let moduleReadsStdin = usedHelperSet.Contains Helpers.readStdinByte
    // Anything that writes to stdout or stderr, or reads stdin, shares the WASI import, the memory and the
    // newline byte.
    let moduleUsesIoHelpers = moduleUsesDbgHelpers || moduleUsesStandardStreams
    let moduleUsesHeap = moduleUsesHeap || usedHelperSet.Contains Helpers.alloc
    let renderedHelpers =
        helperDependencies
        |> List.map fst
        |> List.filter usedHelperSet.Contains
        |> List.map (fun name -> runtimeHelpers |> Map.find name)
        |> fun helpers ->
            // Every function has been lowered by now, so the layouts and open fields are all known.
            if recordTable.OpenFieldIds.Count = 0 then helpers else helpers @ [ renderOpenFieldHelper recordTable ]
        |> String.concat "\n"

    // The `dbg` helpers: `$dbg` prints an integer, `$dbg_str` a string, each prefixed with "dbg: ".
    let dbgFunctions =
            [ "  (func $dbg (param $x i32)"
              "    (local $n i32)"
              "    (local $isneg i32)"
              "    (local $pos i32)"
              "    (local $start i32)"
              "    (local $lo i32)"
              "    (local $hi i32)"
              "    (local $tmp i32)"
              "    local.get $x"
              "    local.set $n"
              "    local.get $n"
              "    i32.const 0"
              "    i32.lt_s"
              "    if"
              "      i32.const 1"
              "      local.set $isneg"
              "      local.get $n"
              "      i32.const -1"
              "      i32.mul"
              "      local.set $n"
              "    end"
              $"    i32.const {dbgDigitBufferOffset}"
              "    local.set $pos"
              "    local.get $isneg"
              "    if"
              "      local.get $pos"
              "      i32.const 45"
              "      i32.store8"
              "      local.get $pos"
              "      i32.const 1"
              "      i32.add"
              "      local.set $pos"
              "    end"
              "    local.get $n"
              "    i32.const 0"
              "    i32.eq"
              "    if"
              "      local.get $pos"
              "      i32.const 48"
              "      i32.store8"
              "      local.get $pos"
              "      i32.const 1"
              "      i32.add"
              "      local.set $pos"
              "    else"
              "      local.get $pos"
              "      local.set $start"
              "      loop $digits"
              "        local.get $pos"
              "        local.get $n"
              "        i32.const 10"
              "        i32.rem_u"
              "        i32.const 48"
              "        i32.add"
              "        i32.store8"
              "        local.get $pos"
              "        i32.const 1"
              "        i32.add"
              "        local.set $pos"
              "        local.get $n"
              "        i32.const 10"
              "        i32.div_u"
              "        local.set $n"
              "        local.get $n"
              "        i32.const 0"
              "        i32.ne"
              "        br_if $digits"
              "      end"
              "      local.get $start"
              "      local.set $lo"
              "      local.get $pos"
              "      i32.const 1"
              "      i32.sub"
              "      local.set $hi"
              "      block $rev_done"
              "        loop $rev"
              "          local.get $lo"
              "          local.get $hi"
              "          i32.ge_u"
              "          br_if $rev_done"
              "          local.get $lo"
              "          i32.load8_u"
              "          local.set $tmp"
              "          local.get $lo"
              "          local.get $hi"
              "          i32.load8_u"
              "          i32.store8"
              "          local.get $hi"
              "          local.get $tmp"
              "          i32.store8"
              "          local.get $lo"
              "          i32.const 1"
              "          i32.add"
              "          local.set $lo"
              "          local.get $hi"
              "          i32.const 1"
              "          i32.sub"
              "          local.set $hi"
              "          br $rev"
              "        end"
              "      end"
              "    end"
              "    local.get $pos"
              "    i32.const 10"
              "    i32.store8"
              "    local.get $pos"
              "    i32.const 1"
              "    i32.add"
              "    local.set $pos"
              "    i32.const 0"
              $"    i32.const {dbgDataBaseOffset}"
              "    i32.store"
              "    i32.const 4"
              "    local.get $pos"
              $"    i32.const {dbgDataBaseOffset}"
              "    i32.sub"
              "    i32.store"
              "    i32.const 1"
              "    i32.const 0"
              "    i32.const 1"
              "    i32.const 8"
              "    call $fd_write"
              "    drop"
              "  )"
              "  (func $dbg_str (param $ptr i32)"
              "    (local $len i32)"
              "    local.get $ptr"
              "    i32.const 4"
              "    i32.sub"
              "    i32.load"
              "    local.set $len"
              "    i32.const 0"
              $"    i32.const {dbgDataBaseOffset}"
              "    i32.store"
              "    i32.const 4"
              "    i32.const 5"
              "    i32.store"
              "    i32.const 1"
              "    i32.const 0"
              "    i32.const 1"
              "    i32.const 8"
              "    call $fd_write"
              "    drop"
              "    i32.const 0"
              "    local.get $ptr"
              "    i32.store"
              "    i32.const 4"
              "    local.get $len"
              "    i32.store"
              "    i32.const 1"
              "    i32.const 0"
              "    i32.const 1"
              "    i32.const 8"
              "    call $fd_write"
              "    drop"
              "    i32.const 0"
              $"    i32.const {dbgStrNewlineOffset}"
              "    i32.store"
              "    i32.const 4"
              "    i32.const 1"
              "    i32.store"
              "    i32.const 1"
              "    i32.const 0"
              "    i32.const 1"
              "    i32.const 8"
              "    call $fd_write"
              "    drop"
              "  )" ]

    let dbgPrelude =
        if moduleUsesIoHelpers then
            [ "  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))"
              if moduleReadsStdin then
                  "  (import \"wasi_snapshot_preview1\" \"fd_read\" (func $fd_read (param i32 i32 i32 i32) (result i32)))"
              "  (memory 1)"
              "  (export \"memory\" (memory 0))"
              if moduleUsesDbgHelpers then
                  $"  (data (i32.const {dbgDataBaseOffset}) \"dbg: \")"
                  $"  (data (i32.const {dbgStrNewlineOffset}) \"\\0a\")"
                  yield! dbgFunctions ]
            |> String.concat "\n"
        else
            ""

    // Every function body has been rendered by now, so the function-value slots are final.
    let contextFnList = functionTable.Values.Values |> Seq.toList

    let contextTypeDefs =
        (contextFnList |> List.map ctxFnArity) @ (functionTable.IndirectArities |> Seq.toList)
        |> Set.ofList
        |> Set.toList
        |> List.sort
        |> List.map (fun arity ->
            let paramSig =
                [ for _ in 1 .. arity -> "i32" ]
                |> String.concat " "
            if String.IsNullOrWhiteSpace paramSig then
                $"  (type $ctx_fn_{arity} (func (result i32)))"
            else
                $"  (type $ctx_fn_{arity} (func (param {paramSig}) (result i32)))")
        |> String.concat "\n"

    // A `call_indirect` needs a table to validate, even in a module that never makes a closure to put in it
    // (a function that only calls the closures it is given).
    let contextTable =
        if contextFnList.IsEmpty && Seq.isEmpty functionTable.IndirectArities then
            ""
        else
            $"  (table $ctx_table {contextFnList.Length} funcref)"

    let contextElem =
        if contextFnList.IsEmpty then
            ""
        else
            let ordered = contextFnList |> List.sortBy ctxFnSlot
            let elems = ordered |> List.map (fun ctxFn -> $"${ctxFnName ctxFn}") |> String.concat " "
            $"  (elem (i32.const 0) {elems})"

    let contextPrelude =
        [ contextTypeDefs; contextTable; contextElem ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n"

    let dbgTagImports =
        dbgTagNames
        |> List.map (fun tagName ->
            let importName = $"dbg_tag_{sanitizeImportSuffix tagName}"
            $"  (import \"env\" \"{importName}\" (func ${importName}))")

    let dbgTagPayloadImports =
        dbgTagPayloadNames
        |> List.map (fun tagName ->
            let importName = $"dbg_tag_payload_{sanitizeImportSuffix tagName}"
            $"  (import \"env\" \"{importName}\" (func ${importName} (param i32)))")

    let dataSegments =
        stringLiteralData
        |> List.map (fun (_, offset, bytes) ->
            $"  (data (i32.const {offset}) \"{escapeWasmData bytes}\")")
        |> String.concat "\n"

    // Add memory if we have string literals, heap allocation or reads of tag cells, but no dbg helpers (dbgPrelude
    // already includes memory)
    let readsTagCells = usedHelperSet.Contains Helpers.tagCode || usedHelperSet.Contains Helpers.tagEquals
    let needsMemory = (not (List.isEmpty stringLiteralData) || moduleUsesHeap || readsTagCells) && not moduleUsesIoHelpers
    let memorySection =
        if needsMemory then
            "  (memory 1)\n  (export \"memory\" (memory 0))"
        else
            ""

    // Add heap global if we use heap allocation
    let heapSection =
        if moduleUsesHeap then
            $"  (global $heap_ptr (mut i32) (i32.const {heapBaseOffset}))"
        else
            ""

    // Imports must precede every function definition in a WebAssembly module, so the host imports for
    // `dbg(#tag)` come before the prelude (which defines $dbg and friends).
    let allImports =
        [ yield! dbgTagImports
          yield! dbgTagPayloadImports
          if not (String.IsNullOrWhiteSpace dbgPrelude) then dbgPrelude
          if not (String.IsNullOrWhiteSpace memorySection) then memorySection
          if not (String.IsNullOrWhiteSpace heapSection) then heapSection
          if not (String.IsNullOrWhiteSpace renderedHelpers) then renderedHelpers
          if not (String.IsNullOrWhiteSpace contextPrelude) then contextPrelude ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n\n"

    if String.IsNullOrWhiteSpace renderedFunctions && String.IsNullOrWhiteSpace allImports && String.IsNullOrWhiteSpace dataSegments then
        "(module)"
    else
        let parts =
            [ if not (String.IsNullOrWhiteSpace allImports) then allImports
              if not (String.IsNullOrWhiteSpace dataSegments) then dataSegments
              if not (String.IsNullOrWhiteSpace renderedFunctions) then renderedFunctions
              if not (String.IsNullOrWhiteSpace renderedStartFunction) then renderedStartFunction
              if not (String.IsNullOrWhiteSpace startDirective) then startDirective ]
        let body = String.concat "\n\n" parts
        $"(module\n{body}\n)"

let transpileModuleToWat (module': Module) : string =
    transpileModuleToWatCore module' Map.empty Map.empty Map.empty Map.empty Map.empty

let transpileTypedModuleToWat (typed: TypedModule) : string =
    let typedBodies =
        typed.Items
        |> List.choose (function
            | TypedDef (TypedValueDef(_, name, _, expr)) -> Some (name, expr)
            | _ -> None)
        |> Map.ofList
    let typedValueTypes =
        typed.Types
        |> Map.toList
        |> List.choose (fun (name, ty) -> trySimpleTypeName ty |> Option.map (fun tyName -> name, tyName))
        |> Map.ofList
    let typedReturnTypes =
        typed.Types
        |> Map.toList
        |> List.choose (fun (name, ty) ->
            match Unifier.unalias ty with
            | TyFunc(_, returnTy) -> trySimpleTypeName returnTy |> Option.map (fun tyName -> name, tyName)
            | _ -> None)
        |> Map.ofList
    transpileModuleToWatCore typed.Module typedBodies typedValueTypes typedReturnTypes typed.Types typed.ContextInstances

/// `transpileTypedModuleToWat`, with what the backend cannot compile reported as a diagnostic at the place in the source
/// it is written: the innermost expression it is in that has one, or else the definition.
let tryTranspileTypedModuleToWat (typed: TypedModule) : Result<string, Diagnostic> =
    try
        Ok (transpileTypedModuleToWat typed)
    with
    | UnsupportedConstruct(message, span) -> Error (Diagnostics.at DiagnosticCodes.UnsupportedByBackend span message)
    | Failure message -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend message)
    | ex -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend $"Internal error: {ex.Message}")
