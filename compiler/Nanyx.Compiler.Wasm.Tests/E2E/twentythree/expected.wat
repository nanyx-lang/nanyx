(module
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))

  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4524))

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
      i32.const 4484
      local.get $size
      i32.add
    else
      i32.const 4520
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
      i32.const 4484
      local.get $size
      i32.add
    else
      i32.const 4520
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
  (func $__nanyx_string_from_memory (param $address i32) (param $len i32) (result i32)
    (local $out i32)
    local.get $len
    call $__nanyx_string_alloc
    local.tee $out
    local.get $address
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
  (func $__nanyx_string_equals (param $a i32) (param $b i32) (result i32)
    (local $len i32)
    (local $i i32)
    local.get $a
    call $__nanyx_string_length
    local.set $len
    local.get $len
    local.get $b
    call $__nanyx_string_length
    i32.ne
    if
      i32.const 0
      return
    end
    block $done
      loop $compare
        local.get $i
        local.get $len
        i32.ge_s
        br_if $done
        local.get $a
        local.get $i
        i32.add
        i32.load8_u
        local.get $b
        local.get $i
        i32.add
        i32.load8_u
        i32.ne
        if
          i32.const 0
          return
        end
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $compare
      end
    end
    i32.const 1
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
(func $__nanyx_i32_add (param $a i32) (param $b i32) (result i32)
  (local $r i64)
  local.get $a
  i64.extend_i32_s
  local.get $b
  i64.extend_i32_s
  i64.add
  local.set $r
  local.get $r
  i64.const -2147483648
  i64.lt_s
  local.get $r
  i64.const 2147483647
  i64.gt_s
  i32.or
  if
    i64.const -9223372036854775808
    i64.const -1
    i64.div_s
    drop
  end
  local.get $r
  i32.wrap_i64
)
(func $__nanyx_i32_sub (param $a i32) (param $b i32) (result i32)
  (local $r i64)
  local.get $a
  i64.extend_i32_s
  local.get $b
  i64.extend_i32_s
  i64.sub
  local.set $r
  local.get $r
  i64.const -2147483648
  i64.lt_s
  local.get $r
  i64.const 2147483647
  i64.gt_s
  i32.or
  if
    i64.const -9223372036854775808
    i64.const -1
    i64.div_s
    drop
  end
  local.get $r
  i32.wrap_i64
)

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_6 (func (param i32 i32 i32 i32 i32 i32) (result i32)))
  (table $ctx_table 6 funcref)
  (elem (i32.const 0) $__lambda_1__unit $__lambda_3__unit $__lambda_5__unit $__lambda_7__unit $__lambda_9__unit $__fn___script)

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 268) "\00\00\00\00\01\00\00\00\0a\00\00\00")
  (data (i32.const 280) "\00\00\00\00\01\00\00\00\0d\00\00\00")
  (data (i32.const 292) "\00\00\00\00\0d\00\00\00Hello, Nanyx!\00\00\00")
  (data (i32.const 316) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 328) "\00\00\00\00\00\00\00\00\01\00\00\00")
  (data (i32.const 340) "\00\00\00\00\00\00\00\00\02\00\00\00")
  (data (i32.const 352) "\00\00\00\00\00\00\00\00\03\00\00\00")
  (data (i32.const 364) "\00\00\00\00\00\00\00\00\04\00\00\00")
  (data (i32.const 376) "\00\00\00\00\00\00\00\00\05\00\00\00")

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

  (func $console_host_fill (param $_ i32) (result i32) (local $offset__in1 i32) (local $start i32) (local $offset__in3 i32) (local $end i32) (local $offset__in5 i32) (local $__inlined27 i32) (local $offset__in6 i32) (local $offset__in8 i32) (local $__inlined33 i32) (local $offset__in11 i32) (local $__inlined37 i32) (local $offset__in12 i32) (local $__inlined40 i32) (local $read i32) (local $offset__in13 i32) (local $offset__in15 i32) (local $__inlined46 i32) (local $offset__in17 i32)
    i32.const 4
    local.set $offset__in1
    i32.const 392
    local.get $offset__in1
    i32.add
    i32.load
    local.set $start
    i32.const 8
    local.set $offset__in3
    i32.const 392
    local.get $offset__in3
    i32.add
    i32.load
    local.set $end
    local.get $start
    local.get $end
    i32.lt_s
    (if (result i32)
      (then
        local.get $end
    local.get $start
    call $__nanyx_i32_sub
      )
      (else
        i32.const 12
    local.set $offset__in5
    i32.const 392
    local.get $offset__in5
    i32.add
    local.set $__inlined27
    i32.const 64
    local.set $offset__in6
    local.get $__inlined27
    i32.const 392
    local.get $offset__in6
    i32.add
    i32.store
    i32.const 0
    drop
    i32.const 16
    local.set $offset__in8
    i32.const 392
    local.get $offset__in8
    i32.add
    local.set $__inlined33
    local.get $__inlined33
    i32.const 4032
    i32.store
    i32.const 0
    drop
    i32.const 12
    local.set $offset__in11
    i32.const 392
    local.get $offset__in11
    i32.add
    local.set $__inlined37
    i32.const 20
    local.set $offset__in12
    i32.const 392
    local.get $offset__in12
    i32.add
    local.set $__inlined40
    i32.const 0
    local.get $__inlined37
    i32.const 1
    local.get $__inlined40
    call $fd_read
    i32.const 0
    i32.eq
    (if (result i32)
      (then
        i32.const 20
    local.set $offset__in13
    i32.const 392
    local.get $offset__in13
    i32.add
    i32.load
      )
      (else
        i32.const 0
      )
    )
    local.set $read
    i32.const 4
    local.set $offset__in15
    i32.const 392
    local.get $offset__in15
    i32.add
    local.set $__inlined46
    local.get $__inlined46
    i32.const 0
    i32.store
    i32.const 0
    drop
    i32.const 8
    local.set $offset__in17
    i32.const 392
    local.get $offset__in17
    i32.add
    local.get $read
    i32.store
    i32.const 0
    drop
    local.get $read
      )
    )
  )
  (export "console_host.fill" (func $console_host_fill))

  (func $console_host_lineEnd (param $held i32) (param $index i32) (param $end i32) (result i32) (local $__inlined10 i32)
    local.get $index
    local.get $end
    i32.ge_s
    (if (result i32)
      (then
        local.get $end
      )
      (else
        local.get $held
    local.get $index
    i32.add
    i32.load8_u
    local.set $__inlined10
    local.get $__inlined10
    i32.const 10
    i32.eq
    (if (result i32)
      (then
        local.get $index
    i32.const 1
    call $__nanyx_i32_add
      )
      (else
        local.get $held
    local.get $index
    i32.const 1
    call $__nanyx_i32_add
    local.get $end
    return_call $console_host_lineEnd
      )
    )
      )
    )
  )
  (export "console_host.lineEnd" (func $console_host_lineEnd))

  (func $console_host_readStdin (param $toLineBreak i32) (result i32) (local $left i32) (local $offset__in1 i32) (local $start i32) (local $stop i32) (local $offset__in3 i32) (local $__inlined17 i32) (local $offset__in4 i32) (local $__inlined22 i32) (local $__inlined24 i32)
    i32.const 0
    call $console_host_fill
    local.set $left
    i32.const 4
    local.set $offset__in1
    i32.const 392
    local.get $offset__in1
    i32.add
    i32.load
    local.set $start
    local.get $toLineBreak
    (if (result i32)
      (then
        i32.const 64
    local.set $offset__in3
    i32.const 392
    local.get $offset__in3
    i32.add
    local.set $__inlined17
    local.get $__inlined17
    local.get $start
    local.get $start
    local.get $left
    call $__nanyx_i32_add
    call $console_host_lineEnd
      )
      (else
        local.get $start
    local.get $left
    call $__nanyx_i32_add
      )
    )
    local.set $stop
    i32.const 4
    local.set $offset__in4
    i32.const 392
    local.get $offset__in4
    i32.add
    local.get $stop
    i32.store
    i32.const 0
    drop
    i32.const 64
    local.get $start
    i32.add
    local.set $__inlined22
    i32.const 392
    local.get $__inlined22
    i32.add
    local.set $__inlined24
    local.get $__inlined24
    local.get $stop
    local.get $start
    call $__nanyx_i32_sub
    call $__nanyx_string_from_memory
  )
  (export "console_host.readStdin" (func $console_host_readStdin))

  (func $console_host_readLineAfter (param $parts i32) (result i32) (local $part i32) (local $__inlined15 i32) (local $__inlined17 i32) (local $__inlined4__in3 i32) (local $suffix__in4 i32) (local $__inlined11__in4_i64 i64) (local $__inlined12__in4_i64 i64) (local $__inlined31 i32) (local $__inlined13__in4_i64 i64) (local $__inlined15__in4_i64 i64) (local $__inlined33_i64 i64) (local $__inlined36 i32) (local $__inlined37 i32) (local $__inlined4__in7 i32) (local $__inlined41 i32) (local $suffix__in8 i32) (local $__inlined11__in1__in8_i64 i64) (local $__inlined12__in1__in8_i64 i64) (local $__inlined15__in8 i32) (local $__inlined13__in1__in8_i64 i64) (local $__inlined15__in1__in8_i64 i64) (local $__inlined56 i32) (local $__inlined16__in8_i64 i64) (local $__inlined18__in8_i64 i64) (local $__match_tmp_0__in2 i32) (local $__owned_0 i32) (local $__owned_1 i32) (local $__owned_2 i32) (local $__owned_3 i32)
    i32.const 1
    call $console_host_readStdin
    local.set $part
    local.get $part
    call $__nanyx_string_length
    i64.extend_i32_s
    i64.const 0
    i64.eq
    local.set $__inlined15
    local.get $__inlined15
    (if (result i32)
      (then
        local.get $part
    call $__nanyx_drop_string
    local.get $parts
    call $__nanyx_dup_list_string
    drop
    local.get $parts
    local.set $__match_tmp_0__in2
    local.get $__match_tmp_0__in2
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0__in2
    call $__nanyx_drop_list_string
    i32.const 1
      )
      (else
        i32.const 1
    (if (result i32)
      (then
        local.get $__match_tmp_0__in2
    call $__nanyx_drop_list_string
    i32.const 0
      )
      (else
        unreachable
      )
    )
      )
    )
    local.set $__inlined17
    local.get $__inlined17
    (if (result i32)
      (then
        local.get $parts
    call $__nanyx_drop_list_string
    i32.const 5
    i32.const 3
    call $__nanyx_tag_box
      )
      (else
        local.get $parts
    i32.const 0
    call $prelude_reverseOnto__string
    local.set $__inlined4__in3
    i32.const 7
    local.get $__inlined4__in3
    i32.const 264
    local.tee $__owned_0
    call $__nanyx_string_join
    local.get $__inlined4__in3
    call $__nanyx_drop_list_string
    local.get $__owned_0
    call $__nanyx_drop_string
    call $__nanyx_tag_box
      )
    )
      )
      (else
        i32.const 276
    local.set $suffix__in4
    local.get $suffix__in4
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined11__in4_i64
    local.get $part
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined12__in4_i64
    local.get $__inlined11__in4_i64
    local.get $__inlined12__in4_i64
    i64.gt_s
    (if (result i32)
      (then
        local.get $suffix__in4
    call $__nanyx_drop_string
    i32.const 0
      )
      (else
        local.get $part
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined13__in4_i64
    local.get $__inlined13__in4_i64
    local.get $suffix__in4
    call $__nanyx_string_length
    i64.extend_i32_s
    call $__nanyx_int_sub
    local.set $__inlined15__in4_i64
    local.get $part
    local.get $__inlined15__in4_i64
    call $__nanyx_int_to_index
    local.get $suffix__in4
    call $__nanyx_string_length
    i64.extend_i32_s
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.tee $__owned_1
    local.get $suffix__in4
    call $__nanyx_string_equals
    local.get $__owned_1
    call $__nanyx_drop_string
    local.get $suffix__in4
    call $__nanyx_drop_string
      )
    )
    local.set $__inlined31
    local.get $__inlined31
    (if (result i32)
      (then
        local.get $part
    call $__nanyx_string_length
    i64.extend_i32_s
    i64.const 1
    call $__nanyx_int_sub
    local.set $__inlined33_i64
    local.get $__inlined33_i64
    i64.const 0
    i64.le_s
    (if (result i32)
      (then
        local.get $part
    call $__nanyx_drop_string
    i32.const 264
      )
      (else
        local.get $part
    i64.const 0
    call $__nanyx_int_to_index
    local.get $__inlined33_i64
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.get $part
    call $__nanyx_drop_string
      )
    )
    local.set $__inlined36
    local.get $__inlined36
    local.get $parts
    call $__nanyx_list_cons
    local.set $__inlined37
    local.get $__inlined37
    i32.const 0
    call $prelude_reverseOnto__string
    local.set $__inlined4__in7
    local.get $__inlined4__in7
    i32.const 264
    local.tee $__owned_2
    call $__nanyx_string_join
    local.get $__inlined4__in7
    call $__nanyx_drop_list_string
    local.get $__owned_2
    call $__nanyx_drop_string
    local.set $__inlined41
    i32.const 288
    local.set $suffix__in8
    local.get $suffix__in8
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined11__in1__in8_i64
    local.get $__inlined41
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined12__in1__in8_i64
    local.get $__inlined11__in1__in8_i64
    local.get $__inlined12__in1__in8_i64
    i64.gt_s
    (if (result i32)
      (then
        i32.const 0
      )
      (else
        local.get $__inlined41
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined13__in1__in8_i64
    local.get $__inlined13__in1__in8_i64
    local.get $suffix__in8
    call $__nanyx_string_length
    i64.extend_i32_s
    call $__nanyx_int_sub
    local.set $__inlined15__in1__in8_i64
    local.get $__inlined41
    local.get $__inlined15__in1__in8_i64
    call $__nanyx_int_to_index
    local.get $suffix__in8
    call $__nanyx_string_length
    i64.extend_i32_s
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.tee $__owned_3
    local.get $suffix__in8
    call $__nanyx_string_equals
    local.get $__owned_3
    call $__nanyx_drop_string
      )
    )
    local.set $__inlined15__in8
    local.get $__inlined15__in8
    (if (result i32)
      (then
        local.get $__inlined41
    call $__nanyx_string_length
    i64.extend_i32_s
    local.set $__inlined16__in8_i64
    local.get $__inlined16__in8_i64
    local.get $suffix__in8
    call $__nanyx_string_length
    i64.extend_i32_s
    local.get $suffix__in8
    call $__nanyx_drop_string
    call $__nanyx_int_sub
    local.set $__inlined18__in8_i64
    local.get $__inlined41
    i64.const 0
    call $__nanyx_int_to_index
    local.get $__inlined18__in8_i64
    call $__nanyx_int_to_index
    call $__nanyx_string_slice
    local.get $__inlined41
    call $__nanyx_drop_string
      )
      (else
        local.get $suffix__in8
    call $__nanyx_drop_string
    local.get $__inlined41
      )
    )
    local.set $__inlined56
    i32.const 7
    local.get $__inlined56
    call $__nanyx_tag_box
      )
      (else
        local.get $part
    local.get $parts
    call $__nanyx_list_cons
    return_call $console_host_readLineAfter
      )
    )
      )
    )
  )
  (export "console_host.readLineAfter" (func $console_host_readLineAfter))

  (func $console_host_readToEndAfter (param $blocks i32) (result i32) (local $block i32) (local $__inlined6 i32) (local $__inlined4__in2 i32) (local $__owned_0 i32)
    i32.const 0
    call $console_host_readStdin
    local.set $block
    local.get $block
    call $__nanyx_string_length
    i64.extend_i32_s
    i64.const 0
    i64.eq
    local.set $__inlined6
    local.get $__inlined6
    (if (result i32)
      (then
        local.get $block
    call $__nanyx_drop_string
    local.get $blocks
    i32.const 0
    call $prelude_reverseOnto__string
    local.set $__inlined4__in2
    local.get $__inlined4__in2
    i32.const 264
    local.tee $__owned_0
    call $__nanyx_string_join
    local.get $__inlined4__in2
    call $__nanyx_drop_list_string
    local.get $__owned_0
    call $__nanyx_drop_string
      )
      (else
        local.get $block
    local.get $blocks
    call $__nanyx_list_cons
    return_call $console_host_readToEndAfter
      )
    )
  )
  (export "console_host.readToEndAfter" (func $console_host_readToEndAfter))

  (func $__host_run__unit (param $entry i32) (result i32) (local $__use_write_0 i32) (local $__use_writeError_2 i32) (local $__use_readLine_4 i32) (local $__use_readToEnd_6 i32) (local $__inlined11 i32)
    i32.const 324
    local.set $__use_write_0
    i32.const 336
    local.set $__use_writeError_2
    i32.const 348
    local.set $__use_readLine_4
    i32.const 360
    local.set $__use_readToEnd_6
    local.get $entry
    i32.const 372
    local.get $__use_readLine_4
    local.get $__use_readToEnd_6
    local.get $__use_write_0
    local.get $__use_writeError_2
    local.get $entry
    i32.load
    call_indirect (type $ctx_fn_6)
    local.set $__inlined11
    i32.const 0
  )

  (func $__lambda_1__unit (param $__env i32) (param $text i32) (result i32) (local $fd__in1 i32) (local $offset__in1__in1 i32) (local $__inlined12__in1 i32) (local $offset__in4__in1 i32) (local $__inlined16__in1 i32) (local $offset__in7__in1 i32) (local $__inlined20__in1 i32) (local $offset__in8__in1 i32) (local $__inlined24__in1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 1
    local.set $fd__in1
    i32.const 12
    local.set $offset__in1__in1
    i32.const 392
    local.get $offset__in1__in1
    i32.add
    local.set $__inlined12__in1
    local.get $__inlined12__in1
    local.get $text
    i32.store
    i32.const 0
    drop
    i32.const 16
    local.set $offset__in4__in1
    i32.const 392
    local.get $offset__in4__in1
    i32.add
    local.set $__inlined16__in1
    local.get $__inlined16__in1
    local.get $text
    call $__nanyx_string_length
    i32.store
    i32.const 0
    drop
    i32.const 12
    local.set $offset__in7__in1
    i32.const 392
    local.get $offset__in7__in1
    i32.add
    local.set $__inlined20__in1
    i32.const 20
    local.set $offset__in8__in1
    local.get $fd__in1
    local.get $__inlined20__in1
    i32.const 1
    i32.const 392
    local.get $offset__in8__in1
    i32.add
    call $fd_write
    local.set $__inlined24__in1
    local.get $text
    drop
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )

  (func $__lambda_3__unit (param $__env i32) (param $text i32) (result i32) (local $fd__in1 i32) (local $offset__in1__in1 i32) (local $__inlined12__in1 i32) (local $offset__in4__in1 i32) (local $__inlined16__in1 i32) (local $offset__in7__in1 i32) (local $__inlined20__in1 i32) (local $offset__in8__in1 i32) (local $__inlined24__in1 i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 2
    local.set $fd__in1
    i32.const 12
    local.set $offset__in1__in1
    i32.const 392
    local.get $offset__in1__in1
    i32.add
    local.set $__inlined12__in1
    local.get $__inlined12__in1
    local.get $text
    i32.store
    i32.const 0
    drop
    i32.const 16
    local.set $offset__in4__in1
    i32.const 392
    local.get $offset__in4__in1
    i32.add
    local.set $__inlined16__in1
    local.get $__inlined16__in1
    local.get $text
    call $__nanyx_string_length
    i32.store
    i32.const 0
    drop
    i32.const 12
    local.set $offset__in7__in1
    i32.const 392
    local.get $offset__in7__in1
    i32.add
    local.set $__inlined20__in1
    i32.const 20
    local.set $offset__in8__in1
    local.get $fd__in1
    local.get $__inlined20__in1
    i32.const 1
    i32.const 392
    local.get $offset__in8__in1
    i32.add
    call $fd_write
    local.set $__inlined24__in1
    local.get $text
    drop
    i32.const 0
    local.get $text
    call $__nanyx_drop_string
  )

  (func $__lambda_5__unit (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
    return_call $console_host_readLineAfter
  )

  (func $__lambda_7__unit (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 7
    i32.const 0
    call $console_host_readToEndAfter
    call $__nanyx_tag_box
  )

  (func $__lambda_9__unit (param $__env i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 0
  )

  (func $__fn___script (param $__env i32) (param $__ctx_flush i32) (param $__ctx_readLine i32) (param $__ctx_readToEnd i32) (param $__ctx_write i32) (param $__ctx_writeError i32) (result i32) (local $value__in1__in1 i32)
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
    i32.const 300
    local.set $value__in1__in1
    local.get $__ctx_write
    local.get $value__in1__in1
    i32.const 276
    call $__nanyx_string_concat
    local.get $value__in1__in1
    call $__nanyx_drop_string
    local.get $__ctx_write
    i32.load
    call_indirect (type $ctx_fn_2)
    drop
    i32.const 0
  )
  (export "__fn___script" (func $__fn___script))

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

  (func $__nanyx_drop_fn_unit_to_tags_some_string (param $p i32)
    ;; (unit -> #some(string))
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

  (func $__nanyx_dup_fn_unit_to_tags_some_string (param $p i32) (result i32)
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
    i32.const 384
    call $__host_run__unit
    drop
  )

  (start $__nanyx_start)
)
