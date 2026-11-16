(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
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

  (global $heap_ptr (mut i32) (i32.const 4356))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $link i32)
    (local $ptr i32)
    (local $found i32)
    (local $rest i32)
    (local $next i32)
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
    i32.const 256
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4352
    end
    local.set $link
    local.get $size
    i32.const 256
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
            i32.const 264
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
              local.tee $next
              i32.const 1
              i32.or
              i32.store
              local.get $rest
              local.get $next
              i32.add
              i32.const 4
              i32.sub
              local.get $next
              i32.store
              local.get $rest
              local.get $ptr
              i32.load
              local.tee $next
              i32.store
              local.get $rest
              local.get $link
              i32.store offset=4
              local.get $link
              local.get $rest
              i32.store
              local.get $next
              if
                local.get $next
                local.get $rest
                i32.store offset=4
              end
              local.get $size
              local.set $found
            else
              local.get $ptr
              i32.load offset=4
              local.get $ptr
              i32.load
              i32.store
              local.get $ptr
              i32.load
              local.tee $next
              if
                local.get $next
                local.get $ptr
                i32.load offset=4
                i32.store offset=4
              end
              local.get $ptr
              local.get $found
              i32.add
              local.get $ptr
              local.get $found
              i32.add
              i32.load
              i32.const -3
              i32.and
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
    (local $header i32)
    (local $size i32)
    (local $next i32)
    (local $joined i32)
    (local $link i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.tee $header
    i32.const -4
    i32.and
    local.set $size
    local.get $size
    i32.const 256
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4352
    end
    local.set $next
    local.get $size
    i32.const 256
    i32.le_u
    if
      local.get $ptr
      local.get $next
      i32.load
      i32.store
      local.get $next
      local.get $ptr
      i32.store
      return
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.ne
    if
      local.get $ptr
      local.get $size
      i32.add
      i32.const 4
      i32.add
      local.tee $next
      i32.const 4
      i32.sub
      i32.load
      local.tee $joined
      i32.const 1
      i32.and
      local.get $joined
      i32.const -4
      i32.and
      local.tee $joined
      i32.const 256
      i32.gt_u
      i32.and
      if
        local.get $next
        i32.load offset=4
        local.get $next
        i32.load
        i32.store
        local.get $next
        i32.load
        local.tee $link
        if
          local.get $link
          local.get $next
          i32.load offset=4
          i32.store offset=4
        end
        local.get $size
        local.get $joined
        i32.add
        i32.const 4
        i32.add
        local.set $size
      end
    end
    local.get $header
    i32.const 2
    i32.and
    if
      local.get $ptr
      i32.const 4
      i32.sub
      local.get $ptr
      i32.const 8
      i32.sub
      i32.load
      local.tee $joined
      i32.sub
      local.set $ptr
      local.get $ptr
      i32.load offset=4
      local.get $ptr
      i32.load
      i32.store
      local.get $ptr
      i32.load
      local.tee $link
      if
        local.get $link
        local.get $ptr
        i32.load offset=4
        i32.store offset=4
      end
      local.get $size
      local.get $joined
      i32.add
      i32.const 4
      i32.add
      local.set $size
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.eq
    if
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
    i32.const 1
    i32.or
    i32.store
    local.get $ptr
    local.get $size
    i32.add
    local.tee $next
    i32.const 4
    i32.sub
    local.get $size
    i32.store
    local.get $next
    local.get $next
    i32.load
    i32.const 2
    i32.or
    i32.store
    local.get $ptr
    i32.const 4352
    i32.load
    local.tee $next
    i32.store
    local.get $ptr
    i32.const 4352
    i32.store offset=4
    local.get $next
    if
      local.get $next
      local.get $ptr
      i32.store offset=4
    end
    i32.const 4352
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
  (func $__nanyx_list_cons_i64 (param $head i64) (param $tail i32) (result i32)
    (local $cell i32)
    i32.const 12
    call $__nanyx_new_cell
    local.tee $cell
    local.get $head
    i64.store
    local.get $cell
    local.get $tail
    i32.store offset=8
    local.get $cell
  )
  (func $__nanyx_list_cons_at_i64 (param $block i32) (param $head i64) (param $tail i32) (result i32)
    (local $cell i32)
    local.get $block
    i32.eqz
    if
      local.get $head
      local.get $tail
      call $__nanyx_list_cons_i64
      return
    end
    local.get $block
    i32.const 4
    i32.add
    local.tee $cell
    local.get $head
    i64.store
    local.get $cell
    local.get $tail
    i32.store offset=8
    local.get $cell
  )
  (func $__nanyx_int_text_length (param $n i64) (param $signed i32) (result i32)
    (local $len i32)
    i32.const 1
    local.set $len
    local.get $signed
    if
      local.get $n
      i64.const 0
      i64.lt_s
      if
        i32.const 2
        local.set $len
        i64.const 0
        local.get $n
        i64.sub
        local.set $n
      end
    end
    block $counted
      loop $count
        local.get $n
        i64.const 10
        i64.lt_u
        br_if $counted
        local.get $len
        i32.const 1
        i32.add
        local.set $len
        local.get $n
        i64.const 10
        i64.div_u
        local.set $n
        br $count
      end
    end
    local.get $len
  )
  (func $__nanyx_int_text_write (param $at i32) (param $n i64) (param $signed i32) (result i32)
    (local $end i32)
    local.get $signed
    if
      local.get $n
      i64.const 0
      i64.lt_s
      if
        local.get $at
        i32.const 45
        i32.store8
        local.get $at
        i32.const 1
        i32.add
        local.set $at
        i64.const 0
        local.get $n
        i64.sub
        local.set $n
      end
    end
    local.get $at
    local.get $n
    i32.const 0
    call $__nanyx_int_text_length
    i32.add
    local.tee $end
    local.set $at
    loop $digit
      local.get $at
      i32.const 1
      i32.sub
      local.tee $at
      local.get $n
      i64.const 10
      i64.rem_u
      i32.wrap_i64
      i32.const 48
      i32.add
      i32.store8
      local.get $n
      i64.const 10
      i64.div_u
      local.tee $n
      i64.const 0
      i64.ne
      br_if $digit
    end
    local.get $end
  )

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_2_i32_i64_to_i32 (func (param i32 i64) (result i32)))
  (table $ctx_table 1 funcref)
  (elem (i32.const 0) $__lambda_16)

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")

  (global $items__built (mut i32) (i32.const 0))
  (global $items__value (mut i32) (i32.const 0))

  (func $prelude_reverseOnto__int (param $arg1 i32) (param $arg2 i32) (result i32) (local $__match_tmp_0 i32) (local $__match_tmp_1 i32) (local $__payload_x_i64 i64) (local $__payload_rest i32) (local $__reuse_1 i32)
    local.get $arg1
    local.set $__match_tmp_0
    local.get $arg2
    local.set $__match_tmp_1
    local.get $__match_tmp_0
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0
    call $__nanyx_drop_list_int
    local.get $__match_tmp_1
      )
      (else
        local.get $__match_tmp_0
      i32.const 0
      i32.ne
    (if (result i32)
      (then
        local.get $__match_tmp_0
      i64.load
        local.set $__payload_x_i64
    local.get $__match_tmp_0
      i32.load offset=8
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
      local.get $__payload_rest
    call $__nanyx_dup_list_int
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
    local.get $__payload_x_i64
    local.get $__match_tmp_1
    call $__nanyx_list_cons_at_i64
    return_call $prelude_reverseOnto__int
      )
      (else
        unreachable
      )
    )
      )
    )
  )

  (func $prelude_filterReversed__int (param $arg1 i32) (param $arg2 i32) (param $arg3 i32) (result i32) (local $__match_tmp_0 i32) (local $__match_tmp_1 i32) (local $__match_tmp_2 i32) (local $__payload_x_i64 i64) (local $__payload_rest i32) (local $__reuse_1 i32)
    local.get $arg1
    local.set $__match_tmp_0
    local.get $arg2
    local.set $__match_tmp_1
    local.get $arg3
    local.set $__match_tmp_2
    local.get $__match_tmp_0
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0
    call $__nanyx_drop_list_int
    local.get $__match_tmp_2
      )
      (else
        local.get $__match_tmp_0
      i32.const 0
      i32.ne
    (if (result i32)
      (then
        local.get $__match_tmp_0
      i64.load
        local.set $__payload_x_i64
    local.get $__match_tmp_0
      i32.load offset=8
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
      local.get $__payload_rest
    call $__nanyx_dup_list_int
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
    local.get $__match_tmp_1
    local.get $__match_tmp_1
    local.get $__payload_x_i64
    local.get $__match_tmp_1
    i32.load
    call_indirect (type $ctx_fn_2_i32_i64_to_i32)
    (if (result i32)
      (then
        local.get $__reuse_1
    local.get $__payload_x_i64
    local.get $__match_tmp_2
    call $__nanyx_list_cons_at_i64
      )
      (else
        local.get $__reuse_1
    if
      local.get $__reuse_1
      call $__nanyx_free
    end
    local.get $__match_tmp_2
      )
    )
    return_call $prelude_filterReversed__int
      )
      (else
        unreachable
      )
    )
      )
    )
  )

  (func $items__build (result i32)
    i64.const 1
    i64.const 2
    i64.const 3
    i64.const 4
    i64.const 5
    i64.const 6
    i64.const 7
    i64.const 8
    i64.const 9
    i64.const 10
    i32.const 0
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
    call $__nanyx_list_cons_i64
  )

  (func $items (result i32)
    global.get $items__built
    i32.eqz
    if
      call $items__build
      global.set $items__value
      i32.const 1
      global.set $items__built
    end
    global.get $items__value
    call $__nanyx_dup_list_int
  )
  (export "items" (func $items))

  (func $__lambda_16 (param $__env i32) (param $-3C-arg-3E-_i64 i64) (result i32)
    local.get $-3C-arg-3E-_i64
    i64.const 2
    i64.rem_s
    i64.const 0
    i64.eq
  )
  (export "__lambda_16" (func $__lambda_16))

  (func $__nanyx_drop_list_int (param $p i32)
    ;; list(int)
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
        i32.load offset=8
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

  (func $__nanyx_dup_list_int (param $p i32) (result i32)
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

  (func $__nanyx_drop_fn_int_to_bool (param $p i32)
    ;; (int -> bool)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    i32.eqz
    if
      return
    end
    global.get $__nanyx_dropping
    if
      local.get $p
      i32.const 8
      i32.sub
      global.get $__nanyx_drop_pending_fn_int_to_bool
      i32.store
      local.get $p
      global.set $__nanyx_drop_pending_fn_int_to_bool
      return
    end
    i32.const 1
    global.set $__nanyx_dropping
    local.get $p
    call $__nanyx_drop_dead_fn_int_to_bool
    call $__nanyx_drop_pending
    i32.const 0
    global.set $__nanyx_dropping
  )

  (func $__nanyx_drop_dead_fn_int_to_bool (param $p i32)
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
  )

  (func $__nanyx_dup_fn_int_to_bool (param $p i32) (result i32)
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

  (global $__nanyx_dropping (mut i32) (i32.const 0))
  (global $__nanyx_drop_pending_fn_int_to_bool (mut i32) (i32.const 0))

  (func $__nanyx_drop_pending
    (local $p i32)
    loop $next
      global.get $__nanyx_drop_pending_fn_int_to_bool
      local.tee $p
      if
        local.get $p
        i32.const 8
        i32.sub
        i32.load
        global.set $__nanyx_drop_pending_fn_int_to_bool
        local.get $p
        call $__nanyx_drop_dead_fn_int_to_bool
        br $next
      end
    end
  )

  (func $__nanyx_start (local $__inlined9__in1__in1__in3 i32) (local $keep__in1__in1__in1__in3 i32) (local $__inlined14__in1__in1__in3 i32) (local $__match_tmp_0__in1__in1__in3_i64 i64) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__for_cursor_0__in1__in1__in3 i32) (local $__for_end_0__in1__in1__in3_i64 i64) (local $__for___for_element_0__in1__in1__in3_i64 i64) (local $__owned_0 i32)
    call $items
    local.set $__inlined9__in1__in1__in3
    i32.const 264
    local.set $keep__in1__in1__in1__in3
    local.get $__inlined9__in1__in1__in3
    local.get $keep__in1__in1__in1__in3
    i32.const 0
    call $prelude_filterReversed__int
    local.get $keep__in1__in1__in1__in3
    call $__nanyx_drop_fn_int_to_bool
    i32.const 0
    call $prelude_reverseOnto__int
    local.set $__inlined14__in1__in1__in3
    local.get $__inlined14__in1__in1__in3
    local.set $__for_cursor_0__in1__in1__in3
    block $__for_done_0
      loop $__for_next_0
        local.get $__for_cursor_0__in1__in1__in3
        i32.eqz
        br_if $__for_done_0
        local.get $__for_cursor_0__in1__in1__in3
        i64.load
        local.set $__for___for_element_0__in1__in1__in3_i64
        local.get $__for___for_element_0__in1__in1__in3_i64
    local.set $__match_tmp_0__in1__in1__in3_i64
    i32.const 0
    local.set $__interp_len
    local.get $__match_tmp_0__in1__in1__in3_i64
    i32.const 1
    call $__nanyx_int_text_length
    local.get $__interp_len
    i32.add
    local.set $__interp_len
    local.get $__interp_len
    call $__nanyx_string_alloc
    local.tee $__interp_base
    local.set $__interp_cursor
    local.get $__interp_cursor
    local.get $__match_tmp_0__in1__in1__in3_i64
    i32.const 1
    call $__nanyx_int_text_write
    local.set $__interp_cursor
    local.get $__interp_base
    local.tee $__owned_0
    call $dbg_str
    i32.const 0
    local.get $__owned_0
    call $__nanyx_drop_string
    drop
    local.get $__match_tmp_0__in1__in1__in3_i64
        drop
        local.get $__for_cursor_0__in1__in1__in3
        i32.load offset=8
        local.set $__for_cursor_0__in1__in1__in3
        br $__for_next_0
      end
    end
    i32.const 0
    local.get $__inlined14__in1__in1__in3
    call $__nanyx_drop_list_int
    drop
    i32.const 0
    drop
    global.get $items__built
    if
      global.get $items__value
      call $__nanyx_drop_list_int
      i32.const 0
      global.set $items__built
    end
  )

  (start $__nanyx_start)
)
