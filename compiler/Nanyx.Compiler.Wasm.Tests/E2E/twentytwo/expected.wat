(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))
  (data (i32.const 128) "dbg: ")
  (data (i32.const 224) "\0a")
  (func $dbg_str (param $ptr i32)
    (local $len i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.set $len
    i32.const 0
    i32.const 128
    i32.store
    i32.const 4
    i32.const 5
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
    i32.const 0
    local.get $ptr
    i32.store
    i32.const 4
    local.get $len
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
    i32.const 0
    i32.const 224
    i32.store
    i32.const 4
    i32.const 1
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )

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
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
    local.get $base
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
  (func $__nanyx_escape_byte (param $c i32) (result i32)
    local.get $c
    i32.const 34
    i32.eq
    if
      i32.const 34
      return
    end
    local.get $c
    i32.const 92
    i32.eq
    if
      i32.const 92
      return
    end
    local.get $c
    i32.const 10
    i32.eq
    if
      i32.const 110
      return
    end
    local.get $c
    i32.const 13
    i32.eq
    if
      i32.const 114
      return
    end
    local.get $c
    i32.const 9
    i32.eq
    if
      i32.const 116
      return
    end
    i32.const 0
  )
  (func $__nanyx_string_quote (param $s i32) (result i32)
    (local $len i32)
    (local $i i32)
    (local $n i32)
    (local $out i32)
    (local $at i32)
    (local $c i32)
    (local $e i32)
    local.get $s
    call $__nanyx_string_length
    local.set $len
    i32.const 2
    local.set $n
    block $measured
      loop $measure
        local.get $i
        local.get $len
        i32.ge_s
        br_if $measured
        local.get $n
        i32.const 1
        i32.add
        local.get $s
        local.get $i
        i32.add
        i32.load8_u
        call $__nanyx_escape_byte
        i32.const 0
        i32.ne
        i32.add
        local.set $n
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $measure
      end
    end
    local.get $n
    call $__nanyx_string_alloc
    local.tee $out
    i32.const 34
    i32.store8
    local.get $out
    i32.const 1
    i32.add
    local.set $at
    i32.const 0
    local.set $i
    block $written
      loop $write
        local.get $i
        local.get $len
        i32.ge_s
        br_if $written
        local.get $s
        local.get $i
        i32.add
        i32.load8_u
        local.tee $c
        call $__nanyx_escape_byte
        local.tee $e
        if
          local.get $at
          i32.const 92
          i32.store8
          local.get $at
          i32.const 1
          i32.add
          local.tee $at
          local.get $e
          i32.store8
        else
          local.get $at
          local.get $c
          i32.store8
        end
        local.get $at
        i32.const 1
        i32.add
        local.set $at
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $write
      end
    end
    local.get $at
    i32.const 34
    i32.store8
    local.get $out
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
  (type $ctx_fn_1_i32_to_i64 (func (param i32) (result i64)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_4 (func (param i32 i32 i32 i32) (result i32)))
  (table $ctx_table 8 funcref)
  (elem (i32.const 0) $__fn_writeStdout $__fn_writeStderr $__fn_readStdinByte $__lambda_3__unit $__nanyx_drop_env__fn_unit_to_int__fn_string_to_unit__fn_string_to_unit $__lambda_5__unit $__lambda_7__unit $__fn___script)

  (data (i32.const 256) "\00\00\00\00\0b\00\00\00Hello world\00")
  (data (i32.const 276) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 288) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 300) "\00\00\00\00\00\00\00\00\01\00\00\00")
  (data (i32.const 312) "\00\00\00\00\00\00\00\00\02\00\00\00")
  (data (i32.const 324) "\00\00\00\00\00\00\00\00\06\00\00\00")
  (data (i32.const 336) "\00\00\00\00\00\00\00\00\07\00\00\00")

  (func $writeStdout (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stdout
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )
  (export "writeStdout" (func $writeStdout))

  (func $writeStderr (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stderr
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )
  (export "writeStderr" (func $writeStderr))

  (func $readStdinByte (param $_ i32) (result i64)
    call $__nanyx_read_stdin_byte
    i64.extend_i32_s
  )
  (export "readStdinByte" (func $readStdinByte))

  (func $ignore__unit (param $_ i32) (result i32)
    i32.const 0
  )

  (func $console_host_withoutCarriageReturn (param $line i32) (result i32) (local $length_i64 i64)
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
  (export "console_host.withoutCarriageReturn" (func $console_host_withoutCarriageReturn))

  (func $console_host_readLineAfter (param $acc i32) (param $__ctx_readStdinByte i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $byte_i64 i64) (local $__owned_0 i32)
    local.get $__ctx_readStdinByte
    call $__nanyx_dup_fn_unit_to_int
    drop
    local.get $__ctx_readStdinByte
    local.get $__ctx_readStdinByte
    i32.load
    call_indirect (type $ctx_fn_1_i32_to_i64)
    local.set $byte_i64
    local.get $byte_i64
    i64.const 0
    i64.lt_s
    (if (result i32)
      (then
        local.get $__ctx_readStdinByte
    call $__nanyx_drop_fn_unit_to_int
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
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
        local.get $__ctx_readStdinByte
    call $__nanyx_drop_fn_unit_to_int
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
    i32.const 7
    local.get $acc
    call $console_host_withoutCarriageReturn
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
    local.get $__ctx_readStdinByte
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readLineAfter
      )
    )
      )
    )
  )
  (export "console_host.readLineAfter" (func $console_host_readLineAfter))

  (func $console_host_readToEndAfter (param $acc i32) (param $__ctx_readStdinByte i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $byte_i64 i64) (local $__owned_0 i32)
    local.get $__ctx_readStdinByte
    call $__nanyx_dup_fn_unit_to_int
    drop
    local.get $__ctx_readStdinByte
    local.get $__ctx_readStdinByte
    i32.load
    call_indirect (type $ctx_fn_1_i32_to_i64)
    local.set $byte_i64
    local.get $byte_i64
    i64.const 0
    i64.lt_s
    (if (result i32)
      (then
        local.get $__ctx_readStdinByte
    call $__nanyx_drop_fn_unit_to_int
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
    local.get $__ctx_readStdinByte
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readToEndAfter
      )
    )
  )
  (export "console_host.readToEndAfter" (func $console_host_readToEndAfter))

  (func $__host_run__unit__known (param $entry i32) (result i32) (local $__ctx_writeStdout i32) (local $__ctx_writeStderr i32) (local $__ctx_readStdinByte i32) (local $__use_write_0 i32) (local $__use_writeError_1 i32) (local $__use_readLine_2 i32) (local $__use_readToEnd_4 i32) (local $__use_flush_6 i32)
    i32.const 296
    local.set $__ctx_writeStdout
    i32.const 308
    local.set $__ctx_writeStderr
    i32.const 320
    local.set $__ctx_readStdinByte
    i32.const 296
    local.set $__use_write_0
    local.get $__use_write_0
    call $__nanyx_drop_fn_string_to_unit
    i32.const 308
    local.set $__use_writeError_1
    local.get $__use_writeError_1
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_readStdinByte
    call $__nanyx_dup_fn_unit_to_int
    drop
    local.get $__ctx_writeStderr
    call $__nanyx_dup_fn_string_to_unit
    drop
    local.get $__ctx_writeStdout
    call $__nanyx_dup_fn_string_to_unit
    drop
    i32.const 16
    i32.const 4
    call $__nanyx_new_object
    i32.const 3
    i32.const 0
    call $__nanyx_record_set
    local.get $__ctx_readStdinByte
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 12
    call $__nanyx_record_set
    local.set $__use_readLine_2
    local.get $__use_readLine_2
    call $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinByte_writeStderr_writeStdout
    i32.const 16
    i32.const 4
    call $__nanyx_new_object
    i32.const 5
    i32.const 0
    call $__nanyx_record_set
    local.get $__ctx_readStdinByte
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 12
    call $__nanyx_record_set
    local.set $__use_readToEnd_4
    local.get $__use_readToEnd_4
    call $__nanyx_drop_fn_unit_to_tags_some_string_ctx_readStdinByte_writeStderr_writeStdout
    i32.const 332
    local.set $__use_flush_6
    local.get $__use_flush_6
    call $__nanyx_drop_fn_unit_to_unit
    local.get $entry
    local.get $entry
    i32.load
    call_indirect (type $ctx_fn_1)
    return_call $ignore__unit
  )

  (func $main (result i32) (local $__dbg_value_i64 i64) (local $__dbg_value_1 i32) (local $__owned_0 i32) (local $__owned_1 i32)
    i64.const 42
    local.set $__dbg_value_i64
    local.get $__dbg_value_i64
    call $__dbg_show_int
    local.tee $__owned_0
    call $dbg_str
    i32.const 0
    local.get $__owned_0
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value_i64
    drop
    i32.const 264
    local.set $__dbg_value_1
    local.get $__dbg_value_1
    call $__dbg_show_string
    local.tee $__owned_1
    call $dbg_str
    i32.const 0
    local.get $__owned_1
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value_1
  )
  (export "main" (func $main))

  (func $__script (result i32)
    call $main
    call $__nanyx_drop_string
    i32.const 0
  )
  (export "__script" (func $__script))

  (func $__fn_writeStdout (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    return_call $writeStdout
  )
  (export "__fn_writeStdout" (func $__fn_writeStdout))

  (func $__fn_writeStderr (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    return_call $writeStderr
  )
  (export "__fn_writeStderr" (func $__fn_writeStderr))

  (func $__lambda_3__unit (param $__env i32) (param $__ctx_readStdinByte i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $_ i32) (local $__ctx_readStdinByte_1 i32) (local $__ctx_writeStderr_1 i32) (local $__ctx_writeStdout_1 i32)
    i32.const 0
    local.set $_
    local.get $__env
    i32.load offset=4
    call $__nanyx_dup_fn_unit_to_int
    local.set $__ctx_readStdinByte_1
    local.get $__ctx_readStdinByte_1
    call $__nanyx_drop_fn_unit_to_int
    local.get $__env
    i32.load offset=8
    call $__nanyx_dup_fn_string_to_unit
    local.set $__ctx_writeStderr_1
    local.get $__ctx_writeStderr_1
    call $__nanyx_drop_fn_string_to_unit
    local.get $__env
    i32.load offset=12
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_int_2_fn_string_to_unit_3_fn_string_to_unit
    local.set $__ctx_writeStdout_1
    local.get $__ctx_writeStdout_1
    call $__nanyx_drop_fn_string_to_unit
    i32.const 284
    local.get $__ctx_readStdinByte
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readLineAfter
  )

  (func $__lambda_5__unit (param $__env i32) (param $__ctx_readStdinByte i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $_ i32) (local $__ctx_readStdinByte_1 i32) (local $__ctx_writeStderr_1 i32) (local $__ctx_writeStdout_1 i32)
    i32.const 0
    local.set $_
    local.get $__env
    i32.load offset=4
    call $__nanyx_dup_fn_unit_to_int
    local.set $__ctx_readStdinByte_1
    local.get $__ctx_readStdinByte_1
    call $__nanyx_drop_fn_unit_to_int
    local.get $__env
    i32.load offset=8
    call $__nanyx_dup_fn_string_to_unit
    local.set $__ctx_writeStderr_1
    local.get $__ctx_writeStderr_1
    call $__nanyx_drop_fn_string_to_unit
    local.get $__env
    i32.load offset=12
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_int_2_fn_string_to_unit_3_fn_string_to_unit
    local.set $__ctx_writeStdout_1
    local.get $__ctx_writeStdout_1
    call $__nanyx_drop_fn_string_to_unit
    i32.const 7
    i32.const 284
    local.get $__ctx_readStdinByte
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    call $console_host_readToEndAfter
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
    call $__nanyx_tag_box
  )

  (func $__lambda_7__unit (param $__env i32) (result i32) (local $_ i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    local.set $_
    i32.const 0
  )

  (func $__fn___script (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    return_call $__script
  )
  (export "__fn___script" (func $__fn___script))

  (func $__fn_readStdinByte (param $__env i32) (result i64) (local $__arg1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    local.set $__arg1
    local.get $__arg1
    return_call $readStdinByte
  )
  (export "__fn_readStdinByte" (func $__fn_readStdinByte))

  (func $__dbg_show_int (param $__dbg_value_i64 i64) (result i32) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__interp_src i32) (local $__interp_start i32) (local $__interp_lo i32) (local $__interp_hi i32) (local $__interp_swap i32) (local $__interp_int i64)
    i32.const 0
    local.set $__interp_len
    local.get $__dbg_value_i64
    local.set $__interp_int
    local.get $__interp_int
    i64.const 0
    i64.lt_s
    if
      local.get $__interp_len
      i32.const 1
      i32.add
      local.set $__interp_len
      i64.const 0
      local.get $__interp_int
      i64.sub
      local.set $__interp_int
    end
    local.get $__interp_int
    i64.eqz
    if
      local.get $__interp_len
      i32.const 1
      i32.add
      local.set $__interp_len
    else
      loop $interp_int_len
        local.get $__interp_len
        i32.const 1
        i32.add
        local.set $__interp_len
        local.get $__interp_int
        i64.const 10
        i64.div_u
        local.set $__interp_int
        local.get $__interp_int
        i64.const 0
        i64.ne
        br_if $interp_int_len
      end
    end
    local.get $__interp_len
    call $__nanyx_string_alloc
    local.tee $__interp_base
    local.set $__interp_cursor
    local.get $__dbg_value_i64
    local.set $__interp_int
    local.get $__interp_int
    i64.const 0
    i64.lt_s
    if
      local.get $__interp_cursor
      i32.const 45
      i32.store8
      local.get $__interp_cursor
      i32.const 1
      i32.add
      local.set $__interp_cursor
      i64.const 0
      local.get $__interp_int
      i64.sub
      local.set $__interp_int
    end
    local.get $__interp_int
    i64.eqz
    if
      local.get $__interp_cursor
      i32.const 48
      i32.store8
      local.get $__interp_cursor
      i32.const 1
      i32.add
      local.set $__interp_cursor
    else
      local.get $__interp_cursor
      local.set $__interp_start
      loop $interp_digits
        local.get $__interp_cursor
        local.get $__interp_int
        i64.const 10
        i64.rem_u
        i32.wrap_i64
        i32.const 48
        i32.add
        i32.store8
        local.get $__interp_cursor
        i32.const 1
        i32.add
        local.set $__interp_cursor
        local.get $__interp_int
        i64.const 10
        i64.div_u
        local.set $__interp_int
        local.get $__interp_int
        i64.const 0
        i64.ne
        br_if $interp_digits
      end
      local.get $__interp_start
      local.set $__interp_lo
      local.get $__interp_cursor
      i32.const 1
      i32.sub
      local.set $__interp_hi
      block $interp_rev_done
        loop $interp_rev
          local.get $__interp_lo
          local.get $__interp_hi
          i32.ge_u
          br_if $interp_rev_done
          local.get $__interp_lo
          i32.load8_u
          local.set $__interp_swap
          local.get $__interp_lo
          local.get $__interp_hi
          i32.load8_u
          i32.store8
          local.get $__interp_hi
          local.get $__interp_swap
          i32.store8
          local.get $__interp_lo
          i32.const 1
          i32.add
          local.set $__interp_lo
          local.get $__interp_hi
          i32.const 1
          i32.sub
          local.set $__interp_hi
          br $interp_rev
        end
      end
    end
    local.get $__interp_base
  )

  (func $__dbg_show_string (param $__dbg_value i32) (result i32)
    local.get $__dbg_value
    call $__nanyx_string_quote
  )

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

  (func $__nanyx_drop_fn_unit_to_int (param $p i32)
    ;; (unit -> int)
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

  (func $__nanyx_dup_fn_unit_to_int (param $p i32) (result i32)
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

  (func $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinByte_writeStderr_writeStdout (param $p i32)
    ;; ([readStdinByte, writeStderr, writeStdout] unit -> #error(#endOfInput) | #some(string))
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

  (func $__nanyx_dup_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinByte_writeStderr_writeStdout (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_unit_to_tags_some_string_ctx_readStdinByte_writeStderr_writeStdout (param $p i32)
    ;; ([readStdinByte, writeStderr, writeStdout] unit -> #some(string))
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

  (func $__nanyx_dup_fn_unit_to_tags_some_string_ctx_readStdinByte_writeStderr_writeStdout (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
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

  (func $__nanyx_drop_rec_0_word_1_fn_unit_to_int_2_fn_string_to_unit_3_fn_string_to_unit (param $p i32)
    ;; (0: word, 1: (unit -> int), 2: (string -> unit), 3: (string -> unit))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=4
      call $__nanyx_drop_fn_unit_to_int
      local.get $p
      i32.load offset=8
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.load offset=12
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word_1_fn_unit_to_int_2_fn_string_to_unit_3_fn_string_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_env__fn_unit_to_int__fn_string_to_unit__fn_string_to_unit (param $p i32) (result i32)
    local.get $p
    i32.load offset=4
    call $__nanyx_drop_fn_unit_to_int
    local.get $p
    i32.load offset=8
    call $__nanyx_drop_fn_string_to_unit
    local.get $p
    i32.load offset=12
    call $__nanyx_drop_fn_string_to_unit
    i32.const 0
  )

  (func $__nanyx_start
    i32.const 344
    call $__host_run__unit__known
    drop
  )

  (start $__nanyx_start)
)