(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4132))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $link i32)
    (local $ptr i32)
    (local $found i32)
    (local $rest i32)
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
    local.get $size
    i32.const 32
    i32.le_u
    if
      local.get $link
      i32.load
      local.tee $ptr
      if
        local.get $link
        local.get $ptr
        i32.load
        i32.store
        local.get $ptr
        i32.const 4
        i32.sub
        local.get $size
        i32.store
        local.get $ptr
        return
      end
    else
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
          i32.const -4
          i32.and
          local.tee $found
          local.get $size
          i32.ge_u
          if
            local.get $found
            local.get $size
            i32.sub
            i32.const 40
            i32.ge_u
            if
              local.get $ptr
              local.get $size
              i32.add
              i32.const 4
              i32.add
              local.tee $rest
              i32.const 4
              i32.sub
              local.get $found
              local.get $size
              i32.sub
              i32.const 4
              i32.sub
              i32.store
              local.get $rest
              local.get $ptr
              i32.load
              i32.store
              local.get $link
              local.get $rest
              i32.store
              local.get $size
              local.set $found
            else
              local.get $link
              local.get $ptr
              i32.load
              i32.store
            end
            local.get $ptr
            i32.const 4
            i32.sub
            local.get $found
            i32.store
            local.get $ptr
            return
          end
          local.get $ptr
          local.set $link
          br $search
        end
      end
    end
    global.get $heap_ptr
    i32.const 4
    i32.add
    local.tee $ptr
    local.get $size
    i32.add
    local.tee $rest
    local.get $ptr
    i32.lt_u
    if
      unreachable
    end
    local.get $rest
    global.set $heap_ptr
    global.get $heap_ptr
    i64.extend_i32_u
    i64.const 65535
    i64.add
    i64.const 16
    i64.shr_u
    i32.wrap_i64
    memory.size
    i32.sub
    local.tee $rest
    i32.const 0
    i32.gt_s
    if
      local.get $rest
      memory.grow
      i32.const -1
      i32.eq
      if
        unreachable
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
    (local $next i32)
    (local $before i32)
    (local $joined i32)
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
    local.get $size
    i32.const 32
    i32.le_u
    if
      local.get $ptr
      local.get $head
      i32.load
      i32.store
      local.get $head
      local.get $ptr
      i32.store
      return
    end
    block $found
      loop $search
        local.get $head
        i32.load
        local.tee $next
        i32.eqz
        br_if $found
        local.get $next
        local.get $ptr
        i32.gt_u
        br_if $found
        local.get $head
        local.set $before
        local.get $next
        local.set $head
        br $search
      end
    end
    local.get $ptr
    local.get $size
    i32.add
    i32.const 4
    i32.add
    local.get $next
    i32.eq
    if
      local.get $size
      local.get $next
      i32.const 4
      i32.sub
      i32.load
      i32.const -4
      i32.and
      i32.add
      i32.const 4
      i32.add
      local.set $size
      local.get $next
      i32.load
      local.set $next
    end
    local.get $before
    if
      local.get $head
      local.get $head
      i32.const 4
      i32.sub
      i32.load
      i32.const -4
      i32.and
      local.tee $joined
      i32.add
      i32.const 4
      i32.add
      local.get $ptr
      i32.eq
      if
        local.get $joined
        local.get $size
        i32.add
        i32.const 4
        i32.add
        local.set $size
        local.get $head
        local.set $ptr
        local.get $before
        local.set $head
      end
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.eq
    if
      local.get $head
      i32.const 0
      i32.store
      local.get $ptr
      i32.const 4
      i32.sub
      global.set $heap_ptr
      return
    end
    local.get $ptr
    i32.const 4
    i32.sub
    local.get $size
    i32.store
    local.get $ptr
    local.get $next
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
    local.get $dst
    local.get $src
    local.get $len
    memory.copy
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
  (global $__nanyx_stdin_buffer (mut i32) (i32.const 0))
  (global $__nanyx_stdin_start (mut i32) (i32.const 0))
  (global $__nanyx_stdin_end (mut i32) (i32.const 0))
  (func $__nanyx_read_stdin (param $toLineBreak i32) (result i32)
    (local $len i32)
    (local $i i32)
    (local $out i32)
    global.get $__nanyx_stdin_start
    global.get $__nanyx_stdin_end
    i32.ge_u
    if
      global.get $__nanyx_stdin_buffer
      i32.eqz
      if
        i32.const 65536
        call $__nanyx_alloc
        global.set $__nanyx_stdin_buffer
      end
      i32.const 0
      global.get $__nanyx_stdin_buffer
      i32.store
      i32.const 4
      i32.const 65536
      i32.store
      i32.const 0
      global.set $__nanyx_stdin_start
      i32.const 0
      i32.const 0
      i32.const 1
      i32.const 8
      call $fd_read
      if (result i32)
        i32.const 0
      else
        i32.const 8
        i32.load
      end
      global.set $__nanyx_stdin_end
    end
    global.get $__nanyx_stdin_end
    global.get $__nanyx_stdin_start
    i32.sub
    local.set $len
    local.get $toLineBreak
    if
      block $found
        loop $scan
          local.get $i
          local.get $len
          i32.ge_u
          br_if $found
          global.get $__nanyx_stdin_buffer
          global.get $__nanyx_stdin_start
          i32.add
          local.get $i
          i32.add
          i32.load8_u
          i32.const 10
          i32.eq
          if
            local.get $i
            i32.const 1
            i32.add
            local.set $len
            br $found
          end
          local.get $i
          i32.const 1
          i32.add
          local.set $i
          br $scan
        end
      end
    end
    local.get $len
    call $__nanyx_string_alloc
    local.tee $out
    global.get $__nanyx_stdin_buffer
    global.get $__nanyx_stdin_start
    i32.add
    local.get $len
    call $__nanyx_copy_bytes
    global.get $__nanyx_stdin_start
    local.get $len
    i32.add
    global.set $__nanyx_stdin_start
    local.get $out
  )
  (func $__nanyx_read_stdin_block (result i32)
    i32.const 0
    call $__nanyx_read_stdin
  )
  (func $__nanyx_read_stdin_line (result i32)
    i32.const 1
    call $__nanyx_read_stdin
  )
  (func $__nanyx_list_cons (param $head i32) (param $tail i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_new_cell
    local.tee $cell
    local.get $head
    i32.store
    local.get $cell
    local.get $tail
    i32.store offset=4
    local.get $cell
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
  (func $__nanyx_list_cons_at (param $block i32) (param $head i32) (param $tail i32) (result i32)
    (local $cell i32)
    local.get $block
    i32.eqz
    if
      local.get $head
      local.get $tail
      call $__nanyx_list_cons
      return
    end
    local.get $block
    i32.const 4
    i32.add
    local.tee $cell
    local.get $head
    i32.store
    local.get $cell
    local.get $tail
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
  (func $__nanyx_string_join (param $pieces i32) (param $separator i32) (result i32)
    (local $separatorLength i32)
    (local $total i32)
    (local $cell i32)
    (local $piece i32)
    (local $length i32)
    (local $out i32)
    (local $at i32)
    local.get $separator
    call $__nanyx_string_length
    local.set $separatorLength
    local.get $pieces
    local.set $cell
    block $measured
      loop $measure
        local.get $cell
        i32.eqz
        br_if $measured
        local.get $total
        local.get $cell
        i32.load
        call $__nanyx_string_length
        i32.add
        local.get $separatorLength
        i32.add
        local.set $total
        local.get $cell
        i32.load offset=4
        local.set $cell
        br $measure
      end
    end
    local.get $pieces
    if
      local.get $total
      local.get $separatorLength
      i32.sub
      local.set $total
    end
    local.get $total
    call $__nanyx_string_alloc
    local.tee $out
    local.set $at
    local.get $pieces
    local.set $cell
    block $copied
      loop $copy
        local.get $cell
        i32.eqz
        br_if $copied
        local.get $cell
        local.get $pieces
        i32.ne
        if
          local.get $at
          local.get $separator
          local.get $separatorLength
          call $__nanyx_copy_bytes
          local.get $at
          local.get $separatorLength
          i32.add
          local.set $at
        end
        local.get $cell
        i32.load
        local.tee $piece
        call $__nanyx_string_length
        local.set $length
        local.get $at
        local.get $piece
        local.get $length
        call $__nanyx_copy_bytes
        local.get $at
        local.get $length
        i32.add
        local.set $at
        local.get $cell
        i32.load offset=4
        local.set $cell
        br $copy
      end
    end
    local.get $out
  )

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_5 (func (param i32 i32 i32 i32 i32) (result i32)))
  (type $ctx_fn_6 (func (param i32 i32 i32 i32 i32 i32) (result i32)))
  (table $ctx_table 13 funcref)
  (elem (i32.const 0) $__fn_writeStdout $__fn_writeStderr $__fn_readStdinLine $__fn_readStdinBlock $__lambda_3__unit $__nanyx_drop_env__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit $__lambda_5__unit $__lambda_7__unit $__adapt_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string $__nanyx_drop_env__fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit $__adapt_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string $__nanyx_drop_env__fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit $__fn_main)

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 268) "\00\00\00\00\0d\00\00\00Hello, World!\00\00\00")
  (data (i32.const 292) "\00\00\00\00\01\00\00\00\0a\00\00\00")
  (data (i32.const 304) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 316) "\00\00\00\00\00\00\00\00\01\00\00\00")
  (data (i32.const 328) "\00\00\00\00\00\00\00\00\02\00\00\00")
  (data (i32.const 340) "\00\00\00\00\00\00\00\00\03\00\00\00")
  (data (i32.const 352) "\00\00\00\00\00\00\00\00\07\00\00\00")
  (data (i32.const 364) "\00\00\00\00\00\00\00\00\0c\00\00\00")

  (func $prelude_reverseOnto__string (param $arg1 i32) (param $arg2 i32) (result i32) (local $__match_tmp_0 i32) (local $__match_tmp_1 i32) (local $__payload_x i32) (local $__payload_rest i32) (local $__reuse_1 i32)
    local.get $arg1
    local.set $__match_tmp_0
    local.get $arg2
    local.set $__match_tmp_1
    local.get $__match_tmp_0
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0
    call $__nanyx_drop_list_string
    local.get $__match_tmp_1
      )
      (else
        local.get $__match_tmp_0
      i32.const 0
      i32.ne
    (if (result i32)
      (then
        local.get $__match_tmp_0
      i32.load
        local.set $__payload_x
    local.get $__match_tmp_0
      i32.load offset=4
        local.set $__payload_rest
    local.get $__match_tmp_0
    i32.const 4
    i32.sub
    i32.load
    i32.const 1
    i32.eq
    if
      local.get $__match_tmp_0
      i32.const 4
      i32.sub
      local.set $__reuse_1
    else
      local.get $__payload_x
    call $__nanyx_dup_string
    drop
    local.get $__payload_rest
    call $__nanyx_dup_list_string
    drop
      local.get $__match_tmp_0
      i32.const 4
      i32.sub
      call $__nanyx_release
      drop
      i32.const 0
      local.set $__reuse_1
    end
    local.get $__payload_rest
    local.get $__reuse_1
    local.get $__payload_x
    local.get $__match_tmp_1
    call $__nanyx_list_cons_at
    return_call $prelude_reverseOnto__string
      )
      (else
        unreachable
      )
    )
      )
    )
  )

  (func $console_host_readLineAfter (param $parts i32) (param $__ctx_readStdinBlock i32) (param $__ctx_readStdinLine i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $part i32) (local $length_i64 i64) (local $__inlined21 i32) (local $__inlined25 i32) (local $__inlined28 i32) (local $length__in4_i64 i64) (local $__inlined36 i32) (local $__match_tmp_0__in1 i32) (local $__owned_0 i32) (local $__owned_1 i32)
    local.get $__ctx_readStdinLine
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_readStdinLine
    local.get $__ctx_readStdinLine
    i32.load
    call_indirect (type $ctx_fn_1)
    local.set $part
    local.get $part
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $length_i64
    local.get $length_i64
    i64.const 0
    i64.eq
    (if (result i32)
      (then
        local.get $__ctx_readStdinBlock
    call $__nanyx_drop_fn_unit_to_string
    local.get $__ctx_readStdinLine
    call $__nanyx_drop_fn_unit_to_string
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
    local.get $part
    call $__nanyx_drop_string
    local.get $parts
    call $__nanyx_dup_list_string
    drop
    local.get $parts
    local.set $__match_tmp_0__in1
    local.get $__match_tmp_0__in1
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0__in1
    call $__nanyx_drop_list_string
    i32.const 1
      )
      (else
        i32.const 1
    (if (result i32)
      (then
        local.get $__match_tmp_0__in1
    call $__nanyx_drop_list_string
    i32.const 0
      )
      (else
        unreachable
      )
    )
      )
    )
    local.set $__inlined21
    local.get $__inlined21
    (if (result i32)
      (then
        local.get $parts
    call $__nanyx_drop_list_string
    i32.const 5
    i32.const 3
    call $__nanyx_tag_box
      )
      (else
        i32.const 7
    local.get $parts
    i32.const 0
    call $prelude_reverseOnto__string
    local.tee $__owned_0
    i32.const 264
    call $__nanyx_string_join
    local.get $__owned_0
    call $__nanyx_drop_list_string
    call $__nanyx_tag_box
      )
    )
      )
      (else
        local.get $part
    local.get $length_i64
    i64.const 1
    call $__nanyx_int_sub
    call $__nanyx_int_to_index
    call $__nanyx_string_byte_at
    i64.extend_i32_s
    i64.const 10
    i64.eq
    (if (result i32)
      (then
        local.get $__ctx_readStdinBlock
    call $__nanyx_drop_fn_unit_to_string
    local.get $__ctx_readStdinLine
    call $__nanyx_drop_fn_unit_to_string
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
    local.get $part
    i64.const 0
    call $__nanyx_int_to_index
    local.get $length_i64
    i64.const 1
    call $__nanyx_int_sub
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.get $part
    call $__nanyx_drop_string
    local.get $parts
    call $__nanyx_list_cons
    local.set $__inlined25
    local.get $__inlined25
    i32.const 0
    call $prelude_reverseOnto__string
    local.tee $__owned_1
    i32.const 264
    call $__nanyx_string_join
    local.get $__owned_1
    call $__nanyx_drop_list_string
    local.set $__inlined28
    local.get $__inlined28
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $length__in4_i64
    local.get $length__in4_i64
    i64.const 0
    i64.gt_s
    local.get $__inlined28
    local.get $length__in4_i64
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
        local.get $__inlined28
    i64.const 0
    call $__nanyx_int_to_index
    local.get $length__in4_i64
    i64.const 1
    call $__nanyx_int_sub
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.get $__inlined28
    call $__nanyx_drop_string
      )
      (else
        local.get $__inlined28
      )
    )
    local.set $__inlined36
    i32.const 7
    local.get $__inlined36
    call $__nanyx_tag_box
      )
      (else
        local.get $part
    local.get $parts
    call $__nanyx_list_cons
    local.get $__ctx_readStdinBlock
    local.get $__ctx_readStdinLine
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readLineAfter
      )
    )
      )
    )
  )
  (export "console_host.readLineAfter" (func $console_host_readLineAfter))

  (func $console_host_readToEndAfter (param $blocks i32) (param $__ctx_readStdinBlock i32) (param $__ctx_readStdinLine i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32) (local $block i32) (local $__owned_0 i32)
    local.get $__ctx_readStdinBlock
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_readStdinBlock
    local.get $__ctx_readStdinBlock
    i32.load
    call_indirect (type $ctx_fn_1)
    local.set $block
    local.get $block
    call $__nanyx_string_length
    i64.extend_i32_s
    i64.const 0
    i64.eq
    (if (result i32)
      (then
        local.get $__ctx_readStdinBlock
    call $__nanyx_drop_fn_unit_to_string
    local.get $block
    call $__nanyx_drop_string
    local.get $blocks
    i32.const 0
    call $prelude_reverseOnto__string
    local.tee $__owned_0
    i32.const 264
    call $__nanyx_string_join
    local.get $__owned_0
    call $__nanyx_drop_list_string
      )
      (else
        local.get $block
    local.get $blocks
    call $__nanyx_list_cons
    local.get $__ctx_readStdinBlock
    local.get $__ctx_readStdinLine
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readToEndAfter
      )
    )
  )
  (export "console_host.readToEndAfter" (func $console_host_readToEndAfter))

  (func $__host_run__unit__known (param $entry i32) (result i32) (local $__ctx_writeStdout i32) (local $__ctx_writeStderr i32) (local $__ctx_readStdinLine i32) (local $__ctx_readStdinBlock i32) (local $__use_write_0 i32) (local $__use_writeError_1 i32) (local $__use_readLine_2 i32) (local $__use_readToEnd_4 i32) (local $__use_flush_6 i32) (local $__use_readLine_2_1 i32) (local $__inlined19 i32)
    i32.const 312
    local.set $__ctx_writeStdout
    i32.const 324
    local.set $__ctx_writeStderr
    i32.const 336
    local.set $__ctx_readStdinLine
    i32.const 348
    local.set $__ctx_readStdinBlock
    i32.const 312
    local.set $__use_write_0
    i32.const 324
    local.set $__use_writeError_1
    local.get $__ctx_readStdinBlock
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_readStdinLine
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_writeStderr
    call $__nanyx_dup_fn_string_to_unit
    drop
    local.get $__ctx_writeStdout
    call $__nanyx_dup_fn_string_to_unit
    drop
    i32.const 20
    i32.const 5
    call $__nanyx_new_object
    i32.const 4
    i32.const 0
    call $__nanyx_record_set
    local.get $__ctx_readStdinBlock
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_readStdinLine
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 12
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 16
    call $__nanyx_record_set
    local.set $__use_readLine_2
    local.get $__ctx_readStdinBlock
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_readStdinLine
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_writeStderr
    call $__nanyx_dup_fn_string_to_unit
    drop
    local.get $__ctx_writeStdout
    call $__nanyx_dup_fn_string_to_unit
    drop
    i32.const 20
    i32.const 5
    call $__nanyx_new_object
    i32.const 6
    i32.const 0
    call $__nanyx_record_set
    local.get $__ctx_readStdinBlock
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_readStdinLine
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 12
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 16
    call $__nanyx_record_set
    local.set $__use_readToEnd_4
    i32.const 360
    local.set $__use_flush_6
    local.get $__ctx_readStdinBlock
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_readStdinLine
    call $__nanyx_dup_fn_unit_to_string
    drop
    local.get $__ctx_writeStderr
    call $__nanyx_dup_fn_string_to_unit
    drop
    local.get $__ctx_writeStdout
    call $__nanyx_dup_fn_string_to_unit
    drop
    i32.const 24
    i32.const 9
    call $__nanyx_new_object
    i32.const 8
    i32.const 0
    call $__nanyx_record_set
    local.get $__use_readLine_2
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_readStdinBlock
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_readStdinLine
    i32.const 12
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 16
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 20
    call $__nanyx_record_set
    local.set $__use_readLine_2_1
    local.get $entry
    local.get $__use_flush_6
    local.get $__use_readLine_2_1
    i32.const 24
    i32.const 11
    call $__nanyx_new_object
    i32.const 10
    i32.const 0
    call $__nanyx_record_set
    local.get $__use_readToEnd_4
    i32.const 4
    call $__nanyx_record_set
    local.get $__ctx_readStdinBlock
    i32.const 8
    call $__nanyx_record_set
    local.get $__ctx_readStdinLine
    i32.const 12
    call $__nanyx_record_set
    local.get $__ctx_writeStderr
    i32.const 16
    call $__nanyx_record_set
    local.get $__ctx_writeStdout
    i32.const 20
    call $__nanyx_record_set
    local.get $__use_write_0
    local.get $__use_writeError_1
    local.get $entry
    i32.load
    call_indirect (type $ctx_fn_6)
    local.set $__inlined19
    i32.const 0
  )

  (func $__fn_writeStdout (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    call $__nanyx_write_stdout
    i32.const 0
    local.get $__arg1
    call $__nanyx_drop_string
  )
  (export "__fn_writeStdout" (func $__fn_writeStdout))

  (func $__fn_writeStderr (param $__env i32) (param $__arg1 i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__arg1
    call $__nanyx_write_stderr
    i32.const 0
    local.get $__arg1
    call $__nanyx_drop_string
  )
  (export "__fn_writeStderr" (func $__fn_writeStderr))

  (func $__lambda_3__unit (param $__env i32) (param $__ctx_readStdinBlock i32) (param $__ctx_readStdinLine i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_string_2_fn_unit_to_string_3_fn_string_to_unit_4_fn_string_to_unit
    i32.const 0
    local.get $__ctx_readStdinBlock
    local.get $__ctx_readStdinLine
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    return_call $console_host_readLineAfter
  )

  (func $__lambda_5__unit (param $__env i32) (param $__ctx_readStdinBlock i32) (param $__ctx_readStdinLine i32) (param $__ctx_writeStderr i32) (param $__ctx_writeStdout i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_string_2_fn_unit_to_string_3_fn_string_to_unit_4_fn_string_to_unit
    i32.const 7
    i32.const 0
    local.get $__ctx_readStdinBlock
    local.get $__ctx_readStdinLine
    local.get $__ctx_writeStderr
    local.get $__ctx_writeStdout
    call $console_host_readToEndAfter
    local.get $__ctx_readStdinLine
    call $__nanyx_drop_fn_unit_to_string
    local.get $__ctx_writeStderr
    call $__nanyx_drop_fn_string_to_unit
    local.get $__ctx_writeStdout
    call $__nanyx_drop_fn_string_to_unit
    call $__nanyx_tag_box
  )

  (func $__lambda_7__unit (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
  )

  (func $__fn_main (param $__env i32) (param $__ctx_flush i32) (param $__ctx_readLine i32) (param $__ctx_readToEnd i32) (param $__ctx_write i32) (param $__ctx_writeError i32) (result i32) (local $value__in1__in1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $__ctx_flush
    call $__nanyx_drop_fn_unit_to_unit
    local.get $__ctx_readLine
    call $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string
    local.get $__ctx_readToEnd
    call $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string
    local.get $__ctx_writeError
    call $__nanyx_drop_fn_string_to_unit
    i32.const 276
    local.set $value__in1__in1
    local.get $__ctx_write
    local.get $value__in1__in1
    i32.const 300
    call $__nanyx_string_concat
    local.get $value__in1__in1
    call $__nanyx_drop_string
    local.get $__ctx_write
    i32.load
    return_call_indirect (type $ctx_fn_2)
  )
  (export "__fn_main" (func $__fn_main))

  (func $__fn_readStdinBlock (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    call $__nanyx_read_stdin_block
  )
  (export "__fn_readStdinBlock" (func $__fn_readStdinBlock))

  (func $__fn_readStdinLine (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    call $__nanyx_read_stdin_line
  )
  (export "__fn_readStdinLine" (func $__fn_readStdinLine))

  (func $__adapt_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string (param $__env i32) (result i32) (local $__adapted i32)
    local.get $__env
    i32.load offset=4
    call $__nanyx_dup_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
    local.set $__adapted
    local.get $__adapted
    local.get $__env
    i32.load offset=8
    call $__nanyx_dup_fn_unit_to_string
    local.get $__env
    i32.load offset=12
    call $__nanyx_dup_fn_unit_to_string
    local.get $__env
    i32.load offset=16
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    i32.load offset=20
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit
    local.get $__adapted
    i32.load
    return_call_indirect (type $ctx_fn_5)
  )
  (export "__adapt_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string" (func $__adapt_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string))

  (func $__adapt_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string (param $__env i32) (result i32) (local $__adapted i32)
    local.get $__env
    i32.load offset=4
    call $__nanyx_dup_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
    local.set $__adapted
    local.get $__adapted
    local.get $__env
    i32.load offset=8
    call $__nanyx_dup_fn_unit_to_string
    local.get $__env
    i32.load offset=12
    call $__nanyx_dup_fn_unit_to_string
    local.get $__env
    i32.load offset=16
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    i32.load offset=20
    call $__nanyx_dup_fn_string_to_unit
    local.get $__env
    call $__nanyx_drop_rec_0_word_1_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit
    local.get $__adapted
    i32.load
    return_call_indirect (type $ctx_fn_5)
  )
  (export "__adapt_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string" (func $__adapt_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_to_fn_unit_to_tags_error_tags_endOfInput_some_string))

  (func $__nanyx_drop_list_string (param $p i32)
    ;; list(string)
    (local $tail i32)
    block $done
      loop $next
        local.get $p
        i32.eqz
        br_if $done
        local.get $p
        i32.const 4
        i32.sub
        call $__nanyx_release
        i32.eqz
        br_if $done
        local.get $p
        i32.load
        call $__nanyx_drop_string
        local.get $p
        i32.load offset=4
        local.set $tail
        local.get $p
        i32.const 4
        i32.sub
        call $__nanyx_free
        local.get $tail
        local.set $p
        br $next
      end
    end
  )

  (func $__nanyx_dup_list_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.eqz
      br_if $shared
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
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

  (func $__nanyx_drop_fn_unit_to_string (param $p i32)
    ;; (unit -> string)
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

  (func $__nanyx_dup_fn_unit_to_string (param $p i32) (result i32)
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

  (func $__nanyx_drop_fn_unit_to_unit_ctx_flush_readLine_readToEnd_write_writeError (param $p i32)
    ;; ([flush, readLine, readToEnd, write, writeError] unit -> unit)
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

  (func $__nanyx_dup_fn_unit_to_unit_ctx_flush_readLine_readToEnd_write_writeError (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout (param $p i32)
    ;; ([readStdinBlock, readStdinLine, writeStderr, writeStdout] unit -> #error(#endOfInput) | #some(string))
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

  (func $__nanyx_dup_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout (param $p i32)
    ;; ([readStdinBlock, readStdinLine, writeStderr, writeStdout] unit -> #some(string))
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

  (func $__nanyx_dup_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout (param $p i32) (result i32)
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

  (func $__nanyx_drop_rec_0_word_1_fn_unit_to_string_2_fn_unit_to_string_3_fn_string_to_unit_4_fn_string_to_unit (param $p i32)
    ;; (0: word, 1: (unit -> string), 2: (unit -> string), 3: (string -> unit), 4: (string -> unit))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=4
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=8
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=12
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.load offset=16
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word_1_fn_unit_to_string_2_fn_unit_to_string_3_fn_string_to_unit_4_fn_string_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_0_word_1_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit (param $p i32)
    ;; (0: word, 1: ([readStdinBlock, readStdinLine, writeStderr, writeStdout] unit -> #error(#endOfInput) | #some(string)), 2: (unit -> string), 3: (unit -> string), 4: (string -> unit), 5: (string -> unit))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=4
      call $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
      local.get $p
      i32.load offset=8
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=12
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=16
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.load offset=20
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word_1_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_0_word_1_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit (param $p i32)
    ;; (0: word, 1: ([readStdinBlock, readStdinLine, writeStderr, writeStdout] unit -> #some(string)), 2: (unit -> string), 3: (unit -> string), 4: (string -> unit), 5: (string -> unit))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=4
      call $__nanyx_drop_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
      local.get $p
      i32.load offset=8
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=12
      call $__nanyx_drop_fn_unit_to_string
      local.get $p
      i32.load offset=16
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.load offset=20
      call $__nanyx_drop_fn_string_to_unit
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word_1_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout_2_fn_unit_to_string_3_fn_unit_to_string_4_fn_string_to_unit_5_fn_string_to_unit (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_env__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit (param $p i32) (result i32)
    local.get $p
    i32.load offset=4
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=8
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=12
    call $__nanyx_drop_fn_string_to_unit
    local.get $p
    i32.load offset=16
    call $__nanyx_drop_fn_string_to_unit
    i32.const 0
  )

  (func $__nanyx_drop_env__fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit (param $p i32) (result i32)
    local.get $p
    i32.load offset=4
    call $__nanyx_drop_fn_unit_to_tags_error_tags_endOfInput_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
    local.get $p
    i32.load offset=8
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=12
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=16
    call $__nanyx_drop_fn_string_to_unit
    local.get $p
    i32.load offset=20
    call $__nanyx_drop_fn_string_to_unit
    i32.const 0
  )

  (func $__nanyx_drop_env__fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout__fn_unit_to_string__fn_unit_to_string__fn_string_to_unit__fn_string_to_unit (param $p i32) (result i32)
    local.get $p
    i32.load offset=4
    call $__nanyx_drop_fn_unit_to_tags_some_string_ctx_readStdinBlock_readStdinLine_writeStderr_writeStdout
    local.get $p
    i32.load offset=8
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=12
    call $__nanyx_drop_fn_unit_to_string
    local.get $p
    i32.load offset=16
    call $__nanyx_drop_fn_string_to_unit
    local.get $p
    i32.load offset=20
    call $__nanyx_drop_fn_string_to_unit
    i32.const 0
  )

  (func $__nanyx_start
    i32.const 372
    call $__host_run__unit__known
    drop
  )

  (start $__nanyx_start)
)
