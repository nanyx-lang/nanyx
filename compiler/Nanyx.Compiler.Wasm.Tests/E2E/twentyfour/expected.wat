(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4132))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $link i32)
    (local $ptr i32)
    local.get $size
    i32.const 3
    i32.add
    i32.const -4
    i32.and
    local.tee $size
    i32.eqz
    if
      i32.const 4
      local.set $size
    end
    local.get $size
    i32.const 32
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4128
    end
    local.set $link
    block $empty
      loop $search
        local.get $link
        i32.load
        local.tee $ptr
        i32.eqz
        br_if $empty
        local.get $ptr
        i32.const 4
        i32.sub
        i32.load
        local.get $size
        i32.ge_u
        if
          local.get $link
          local.get $ptr
          i32.load
          i32.store
          local.get $ptr
          return
        end
        local.get $ptr
        local.set $link
        br $search
      end
    end
    global.get $heap_ptr
    i32.const 4
    i32.add
    local.tee $ptr
    local.get $size
    i32.add
    global.set $heap_ptr
    block $fits
      loop $grow
        global.get $heap_ptr
        memory.size
        i32.const 65536
        i32.mul
        i32.le_u
        br_if $fits
        i32.const 1
        memory.grow
        i32.const -1
        i32.eq
        if
          unreachable
        end
        br $grow
      end
    end
    local.get $ptr
    i32.const 4
    i32.sub
    local.get $size
    i32.store
    local.get $ptr
  )
  (func $__nanyx_free (param $ptr i32)
    (local $size i32)
    (local $head i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.set $size
    local.get $size
    i32.const 32
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4128
    end
    local.set $head
    local.get $ptr
    local.get $head
    i32.load
    i32.store
    local.get $head
    local.get $ptr
    i32.store
  )
  (func $__nanyx_new_cell (param $size i32) (result i32)
    (local $block i32)
    local.get $size
    i32.const 4
    i32.add
    call $__nanyx_alloc
    local.tee $block
    i32.const 1
    i32.store
    local.get $block
    i32.const 4
    i32.add
  )
  (func $__nanyx_new_object (param $size i32) (param $info i32) (result i32)
    (local $block i32)
    local.get $size
    i32.const 8
    i32.add
    call $__nanyx_alloc
    local.tee $block
    i32.const 1
    i32.store
    local.get $block
    local.get $info
    i32.store offset=4
    local.get $block
    i32.const 8
    i32.add
  )
  (func $__nanyx_release (param $count i32) (result i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    i32.eqz
    if
      i32.const 0
      return
    end
    local.get $n
    i32.const 1
    i32.eq
    if
      i32.const 1
      return
    end
    local.get $count
    local.get $n
    i32.const 1
    i32.sub
    i32.store
    i32.const 0
  )
  (func $__nanyx_retain (param $count i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    if
      local.get $count
      local.get $n
      i32.const 1
      i32.add
      i32.store
    end
  )
  (func $__nanyx_copy_bytes (param $dst i32) (param $src i32) (param $len i32)
    (local $i i32)
    block $done
      loop $copy
        local.get $i
        local.get $len
        i32.ge_s
        br_if $done
        local.get $dst
        local.get $i
        i32.add
        local.get $src
        local.get $i
        i32.add
        i32.load8_u
        i32.store8
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $copy
      end
    end
  )
  (func $__nanyx_string_length (param $s i32) (result i32)
    local.get $s
    i32.const 4
    i32.sub
    i32.load
  )
  (func $__nanyx_string_alloc (param $len i32) (result i32)
    (local $s i32)
    local.get $len
    i32.const 1
    i32.add
    local.get $len
    call $__nanyx_new_object
    local.tee $s
    local.get $len
    i32.add
    i32.const 0
    i32.store8
    local.get $s
  )
  (func $__nanyx_string_byte_at (param $s i32) (param $i i32) (result i32)
    local.get $i
    i32.const 0
    i32.lt_s
    local.get $i
    local.get $s
    call $__nanyx_string_length
    i32.ge_s
    i32.or
    if (result i32)
      i32.const -1
    else
      local.get $s
      local.get $i
      i32.add
      i32.load8_u
    end
  )
  (func $__nanyx_string_slice (param $s i32) (param $start i32) (param $len i32) (result i32)
    (local $total i32)
    (local $out i32)
    local.get $s
    call $__nanyx_string_length
    local.set $total
    local.get $start
    i32.const 0
    i32.lt_s
    if
      i32.const 0
      local.set $start
    end
    local.get $start
    local.get $total
    i32.gt_s
    if
      local.get $total
      local.set $start
    end
    local.get $len
    i32.const 0
    i32.lt_s
    if
      i32.const 0
      local.set $len
    end
    local.get $len
    local.get $total
    local.get $start
    i32.sub
    i32.gt_s
    if
      local.get $total
      local.get $start
      i32.sub
      local.set $len
    end
    local.get $len
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $s
    local.get $start
    i32.add
    local.get $len
    call $__nanyx_copy_bytes
    local.get $out
  )
  (func $__nanyx_string_from_byte (param $byte i32) (result i32)
    (local $out i32)
    i32.const 1
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $byte
    i32.store8
    local.get $out
  )
  (func $__nanyx_string_concat (param $a i32) (param $b i32) (result i32)
    (local $lenA i32)
    (local $lenB i32)
    (local $out i32)
    local.get $a
    call $__nanyx_string_length
    local.set $lenA
    local.get $b
    call $__nanyx_string_length
    local.set $lenB
    local.get $lenA
    local.get $lenB
    i32.add
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $a
    local.get $lenA
    call $__nanyx_copy_bytes
    local.get $out
    local.get $lenA
    i32.add
    local.get $b
    local.get $lenB
    call $__nanyx_copy_bytes
    local.get $out
  )
  (func $__nanyx_write_stdout (param $s i32)
    i32.const 0
    local.get $s
    i32.store
    i32.const 4
    local.get $s
    call $__nanyx_string_length
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )
  (func $__nanyx_write_stderr (param $s i32)
    i32.const 0
    local.get $s
    i32.store
    i32.const 4
    local.get $s
    call $__nanyx_string_length
    i32.store
    i32.const 2
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )
  (func $__nanyx_read_stdin_byte (result i32)
    i32.const 0
    i32.const 12
    i32.store
    i32.const 4
    i32.const 1
    i32.store
    i32.const 0
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_read
    if (result i32)
      i32.const -1
    else
      i32.const 8
      i32.load
      if (result i32)
        i32.const 12
        i32.load8_u
      else
        i32.const -1
      end
    end
  )
  (func $__nanyx_tag_box (param $code i32) (param $payload i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_new_cell
    local.tee $cell
    local.get $code
    i32.store
    local.get $cell
    local.get $payload
    i32.store offset=4
    local.get $cell
  )
  (func $__nanyx_int_sub (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    local.get $a
    local.get $b
    i64.sub
    local.set $r
    local.get $a
    local.get $b
    i64.xor
    local.get $a
    local.get $r
    i64.xor
    i64.and
    i64.const 0
    i64.lt_s
    if
      i64.const -9223372036854775808
      i64.const -1
      i64.div_s
      drop
    end
    local.get $r
  )
  (func $__nanyx_int_to_index (param $value i64) (result i32)
    local.get $value
    i64.const -2147483648
    i64.lt_s
    if
      i32.const -2147483648
      return
    end
    local.get $value
    i64.const 2147483647
    i64.gt_s
    if
      i32.const 2147483647
      return
    end
    local.get $value
    i32.wrap_i64
  )

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (table $ctx_table 6 funcref)
  (elem (i32.const 0) $__fn_flush $__fn_readLine $__fn_readToEnd $__fn_write $__fn_writeError $__fn_string_toString)

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 268) "\00\00\00\00\0d\00\00\00Hello, World!\00\00\00")
  (data (i32.const 292) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 304) "\00\00\00\00\00\00\00\00\01\00\00\00")
  (data (i32.const 316) "\00\00\00\00\00\00\00\00\02\00\00\00")
  (data (i32.const 328) "\00\00\00\00\00\00\00\00\03\00\00\00")
  (data (i32.const 340) "\00\00\00\00\00\00\00\00\04\00\00\00")
  (data (i32.const 352) "\00\00\00\00\00\00\00\00\05\00\00\00")

  (func $write (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stdout
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )
  (export "write" (func $write))

  (func $writeError (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stderr
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )
  (export "writeError" (func $writeError))

  (func $println__string (param $value i32) (param $__ctx_flush i32) (param $__ctx_readLine i32) (param $__ctx_readToEnd i32) (param $__ctx_write i32) (param $__ctx_writeError i32) (param $__ctx_ToString_a_toString i32) (result i32)
    local.get $__ctx_write
    call $__nanyx_dup_fn_string_to_unit
    drop
    local.get $__ctx_write
    local.get $__ctx_ToString_a_toString
    local.get $value
    local.get $__ctx_ToString_a_toString
    i32.load
    call_indirect (type $ctx_fn_2)
    local.get $__ctx_write
    i32.load
    call_indirect (type $ctx_fn_2)
    drop
    local.get $__ctx_write
    i64.const 10
    i32.wrap_i64
    call $__nanyx_string_from_byte
    local.get $__ctx_write
    i32.load
    return_call_indirect (type $ctx_fn_2)
  )

  (func $console_withoutCarriageReturn (param $line i32) (result i32) (local $length_i64 i64)
    local.get $line
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $length_i64
    local.get $length_i64
    i64.const 0
    i64.gt_s
    local.get $line
    local.get $length_i64
    i64.const 1
    call $__nanyx_int_sub
    call $__nanyx_int_to_index
    call $__nanyx_string_byte_at
    i64.extend_i32_s
    i64.const 13
    i64.eq
    i32.and
    (if (result i32)
      (then
        local.get $line
    i64.const 0
    call $__nanyx_int_to_index
    local.get $length_i64
    i64.const 1
    call $__nanyx_int_sub
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.get $line
    call $__nanyx_drop_string
      )
      (else
        local.get $line
      )
    )
  )
  (export "console.withoutCarriageReturn" (func $console_withoutCarriageReturn))

  (func $console_readLineAfter (param $acc i32) (result i32) (local $byte_i64 i64) (local $__owned_0 i32)
    call $__nanyx_read_stdin_byte
    i64.extend_i32_s
    local.set $byte_i64
    local.get $byte_i64
    i64.const 0
    i64.lt_s
    (if (result i32)
      (then
        local.get $acc
    call $__nanyx_string_length
    i64.extend_i32_s
    i64.const 0
    i64.eq
    (if (result i32)
      (then
        local.get $acc
    call $__nanyx_drop_string
    i32.const 5
    i32.const 3
    call $__nanyx_tag_box
      )
      (else
        i32.const 7
    local.get $acc
    call $__nanyx_tag_box
      )
    )
      )
      (else
        local.get $byte_i64
    i64.const 10
    i64.eq
    (if (result i32)
      (then
        i32.const 7
    local.get $acc
    call $console_withoutCarriageReturn
    call $__nanyx_tag_box
      )
      (else
        local.get $acc
    local.get $byte_i64
    i32.wrap_i64
    call $__nanyx_string_from_byte
    local.tee $__owned_0
    call $__nanyx_string_concat
    local.get $acc
    call $__nanyx_drop_string
    local.get $__owned_0
    call $__nanyx_drop_string
    return_call $console_readLineAfter
      )
    )
      )
    )
  )
  (export "console.readLineAfter" (func $console_readLineAfter))

  (func $readLine (param $_ i32) (result i32)
    i32.const 264
    return_call $console_readLineAfter
  )
  (export "readLine" (func $readLine))

  (func $console_readToEndAfter (param $acc i32) (result i32) (local $byte_i64 i64) (local $__owned_0 i32)
    call $__nanyx_read_stdin_byte
    i64.extend_i32_s
    local.set $byte_i64
    local.get $byte_i64
    i64.const 0
    i64.lt_s
    (if (result i32)
      (then
        local.get $acc
      )
      (else
        local.get $acc
    local.get $byte_i64
    i32.wrap_i64
    call $__nanyx_string_from_byte
    local.tee $__owned_0
    call $__nanyx_string_concat
    local.get $acc
    call $__nanyx_drop_string
    local.get $__owned_0
    call $__nanyx_drop_string
    return_call $console_readToEndAfter
      )
    )
  )
  (export "console.readToEndAfter" (func $console_readToEndAfter))

  (func $readToEnd (param $_ i32) (result i32)
    i32.const 7
    i32.const 264
    call $console_readToEndAfter
    call $__nanyx_tag_box
  )
  (export "readToEnd" (func $readToEnd))

  (func $flush (param $_ i32) (result i32)
    i32.const 0
  )
  (export "flush" (func $flush))

  (func $string_toString (param $s i32) (result i32)
    local.get $s
  )
  (export "string.toString" (func $string_toString))

  (func $main (result i32)
    i32.const 276
    i32.const 300
    i32.const 312
    i32.const 324
    i32.const 336
    i32.const 348
    i32.const 360
    return_call $println__string
  )
  (export "main" (func $main))

  (func $__fn_write (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    return_call $write
  )
  (export "__fn_write" (func $__fn_write))

  (func $__fn_writeError (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    return_call $writeError
  )
  (export "__fn_writeError" (func $__fn_writeError))

  (func $__fn_readLine (param $__env i32) (result i32) (local $__arg1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    local.set $__arg1
    local.get $__arg1
    return_call $readLine
  )
  (export "__fn_readLine" (func $__fn_readLine))

  (func $__fn_readToEnd (param $__env i32) (result i32) (local $__arg1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    local.set $__arg1
    local.get $__arg1
    return_call $readToEnd
  )
  (export "__fn_readToEnd" (func $__fn_readToEnd))

  (func $__fn_flush (param $__env i32) (result i32) (local $__arg1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    local.set $__arg1
    local.get $__arg1
    return_call $flush
  )
  (export "__fn_flush" (func $__fn_flush))

  (func $__fn_string_toString (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    return_call $string_toString
  )
  (export "__fn_string_toString" (func $__fn_string_toString))

  (func $__nanyx_drop_string (param $p i32)
    ;; string
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_unit_to_unit (param $p i32)
    ;; (unit -> unit)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_unit_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string (param $p i32)
    ;; (unit -> #error(#endOfInput) | #some(string))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_unit_to_tags_error_tags_endOfInput_some_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_string_to_unit (param $p i32)
    ;; (string -> unit)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_string_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_string_to_string (param $p i32)
    ;; (string -> string)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_string_to_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_tags_error_tags_endOfInput_some_string (param $p i32)
    ;; #error(#endOfInput) | #some(string)
    local.get $p
    i32.const 1
    i32.and
    if
      return
    end
    local.get $p
    i32.const 4
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load
      i32.const 7
      i32.eq
      if
        local.get $p
        i32.load offset=4
        call $__nanyx_drop_string
      end
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_tags_error_tags_endOfInput_some_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 1
      i32.and
      br_if $shared
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_tags_error_tags_endOfInput (param $p i32)
    ;; #error(#endOfInput)
    local.get $p
    i32.const 1
    i32.and
    if
      return
    end
    local.get $p
    i32.const 4
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_tags_error_tags_endOfInput (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 1
      i32.and
      br_if $shared
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_tags_some_string (param $p i32)
    ;; #some(string)
    local.get $p
    i32.const 1
    i32.and
    if
      return
    end
    local.get $p
    i32.const 4
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=4
      call $__nanyx_drop_string
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_tags_some_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 1
      i32.and
      br_if $shared
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_0_word (param $p i32)
    ;; (0: word)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_start
    call $main
    drop
  )

  (start $__nanyx_start)
)