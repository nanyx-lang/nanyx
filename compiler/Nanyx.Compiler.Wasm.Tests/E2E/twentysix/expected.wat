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
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
    local.get $base
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

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_2_i32_i64_to_i32 (func (param i32 i64) (result i32)))
  (table $ctx_table 2 funcref)
  (elem (i32.const 0) $__lambda_0 $__nanyx_drop_env)

  (func $prelude_reverseOnto__int (param $arg1 i32) (param $arg2 i32) (result i32) (local $__match_tmp_0 i32) (local $__match_tmp_1 i32) (local $__payload_x_i64 i64) (local $__payload_rest i32) (local $__reuse_1 i32)
    local.get $arg1
    local.set $__match_tmp_0
    local.get $arg2
    local.set $__match_tmp_1
    local.get $__match_tmp_0
      i32.eqz
      i32.const 1
      i32.and
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
      i32.const 1
      i32.and
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

  (func $prelude_filterReversed__int (param $arg1 i32) (param $arg2 i32) (param $arg3 i32) (result i32) (local $__match_tmp_0 i32) (local $__match_tmp_1 i32) (local $__match_tmp_2 i32) (local $__payload_x_i64 i64) (local $__payload_rest i32)
    local.get $arg1
    local.set $__match_tmp_0
    local.get $arg2
    local.set $__match_tmp_1
    local.get $arg3
    local.set $__match_tmp_2
    local.get $__match_tmp_0
      i32.eqz
      i32.const 1
      i32.const 1
      i32.and
      i32.and
    (if (result i32)
      (then
        local.get $__match_tmp_0
    call $__nanyx_drop_list_int
    local.get $__match_tmp_1
    call $__nanyx_drop_fn_int_to_bool
    local.get $__match_tmp_2
      )
      (else
        local.get $__match_tmp_0
      i32.const 0
      i32.ne
      i32.const 1
      i32.const 1
      i32.and
      i32.and
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
      call $__nanyx_free
    else
      local.get $__payload_rest
    call $__nanyx_dup_list_int
    drop
      local.get $__match_tmp_0
      i32.const 4
      i32.sub
      call $__nanyx_release
      drop
    end
    local.get $__match_tmp_1
    call $__nanyx_dup_fn_int_to_bool
    drop
    local.get $__payload_rest
    local.get $__match_tmp_1
    local.get $__match_tmp_1
    local.get $__payload_x_i64
    local.get $__match_tmp_1
    i32.load
    call_indirect (type $ctx_fn_2_i32_i64_to_i32)
    (if (result i32)
      (then
        local.get $__payload_x_i64
    local.get $__match_tmp_2
    call $__nanyx_list_cons_i64
      )
      (else
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

  (func $list_filter__int (param $xs i32) (param $keep i32) (result i32)
    local.get $xs
    local.get $keep
    i32.const 0
    call $prelude_filterReversed__int
    i32.const 0
    return_call $prelude_reverseOnto__int
  )

  (func $items (result i32)
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
  (export "items" (func $items))

  (func $main (result i32) (local $__dbg_value_i64 i64) (local $__match_tmp_0_i64 i64) (local $__for_cursor_0 i32) (local $__for_end_0_i64 i64) (local $__for___for_element_0_i64 i64) (local $__owned_0 i32) (local $__owned_1 i32)
    call $items
    i32.const 4
    i32.const 1
    call $__nanyx_new_object
    i32.const 0
    i32.const 0
    call $__nanyx_record_set
    call $list_filter__int
    local.tee $__owned_0
    local.set $__for_cursor_0
    block $__for_done_0
      loop $__for_next_0
        local.get $__for_cursor_0
        i32.eqz
        br_if $__for_done_0
        local.get $__for_cursor_0
        i64.load
        local.set $__for___for_element_0_i64
        local.get $__for___for_element_0_i64
    local.set $__match_tmp_0_i64
    i32.const 1
    (if (result i64)
      (then
        local.get $__match_tmp_0_i64
    local.set $__dbg_value_i64
    local.get $__dbg_value_i64
    call $__dbg_show_int
    local.tee $__owned_1
    call $dbg_str
    i32.const 0
    local.get $__owned_1
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value_i64
      )
      (else
        unreachable
      )
    )
        drop
        local.get $__for_cursor_0
        i32.load offset=8
        local.set $__for_cursor_0
        br $__for_next_0
      end
    end
    i32.const 0
    local.get $__owned_0
    call $__nanyx_drop_list_int
  )
  (export "main" (func $main))

  (func $__lambda_0 (param $__env i32) (param $-3C-arg-3E-_i64 i64) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $-3C-arg-3E-_i64
    i64.const 2
    i64.rem_s
    i64.const 0
    i64.eq
  )
  (export "__lambda_0" (func $__lambda_0))

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

  (func $__nanyx_dup_fn_int_to_bool (param $p i32) (result i32)
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

  (func $__nanyx_drop_env (param $p i32) (result i32)
    i32.const 0
  )

  (func $__nanyx_start
    call $main
    drop
  )

  (start $__nanyx_start)
)