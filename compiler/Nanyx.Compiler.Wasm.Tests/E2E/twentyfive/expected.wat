(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))
  (data (i32.const 128) "dbg: ")
  (data (i32.const 224) "\0a")
  (func $dbg (param $x i32)
    (local $n i32)
    (local $isneg i32)
    (local $pos i32)
    (local $start i32)
    (local $lo i32)
    (local $hi i32)
    (local $tmp i32)
    local.get $x
    local.set $n
    local.get $n
    i32.const 0
    i32.lt_s
    if
      i32.const 1
      local.set $isneg
      local.get $n
      i32.const -1
      i32.mul
      local.set $n
    end
    i32.const 133
    local.set $pos
    local.get $isneg
    if
      local.get $pos
      i32.const 45
      i32.store8
      local.get $pos
      i32.const 1
      i32.add
      local.set $pos
    end
    local.get $n
    i32.const 0
    i32.eq
    if
      local.get $pos
      i32.const 48
      i32.store8
      local.get $pos
      i32.const 1
      i32.add
      local.set $pos
    else
      local.get $pos
      local.set $start
      loop $digits
        local.get $pos
        local.get $n
        i32.const 10
        i32.rem_u
        i32.const 48
        i32.add
        i32.store8
        local.get $pos
        i32.const 1
        i32.add
        local.set $pos
        local.get $n
        i32.const 10
        i32.div_u
        local.set $n
        local.get $n
        i32.const 0
        i32.ne
        br_if $digits
      end
      local.get $start
      local.set $lo
      local.get $pos
      i32.const 1
      i32.sub
      local.set $hi
      block $rev_done
        loop $rev
          local.get $lo
          local.get $hi
          i32.ge_u
          br_if $rev_done
          local.get $lo
          i32.load8_u
          local.set $tmp
          local.get $lo
          local.get $hi
          i32.load8_u
          i32.store8
          local.get $hi
          local.get $tmp
          i32.store8
          local.get $lo
          i32.const 1
          i32.add
          local.set $lo
          local.get $hi
          i32.const 1
          i32.sub
          local.set $hi
          br $rev
        end
      end
    end
    local.get $pos
    i32.const 10
    i32.store8
    local.get $pos
    i32.const 1
    i32.add
    local.set $pos
    i32.const 0
    i32.const 128
    i32.store
    i32.const 4
    local.get $pos
    i32.const 128
    i32.sub
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )
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
  (func $__nanyx_list_cons (param $head i32) (param $tail i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_new_cell
    local.set $cell
    local.get $cell
    local.get $head
    i32.store
    local.get $cell
    i32.const 4
    i32.add
    local.get $tail
    i32.store
    local.get $cell
  )

  (func $sum (param $xs i32) (result i32) (local $__match_tmp_0 i32) (local $__payload_x i32) (local $__payload_rest i32)
    local.get $xs
    local.set $__match_tmp_0
    local.get $__match_tmp_0
      i32.eqz
    (if (result i32)
      (then
        local.get $__match_tmp_0
    call $__nanyx_drop_list_int
    i32.const 0
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
      i32.const 4
      i32.add
      i32.load
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
    local.get $__payload_x
    local.get $__payload_rest
    call $sum
    i32.add
      )
      (else
        unreachable
      )
    )
      )
    )
  )
  (export "sum" (func $sum))

  (func $main (result i32) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__interp_src i32) (local $__interp_tmp i32) (local $__interp_start i32) (local $__interp_lo i32) (local $__interp_hi i32) (local $__interp_swap i32) (local $__interp_hole_0_0 i32) (local $__dbg_tmp i32)
    i32.const 1
    i32.const 2
    i32.const 3
    i32.const 0
    call $__nanyx_list_cons
    call $__nanyx_list_cons
    call $__nanyx_list_cons
    call $sum
    local.tee $__dbg_tmp
    call $dbg
    local.get $__dbg_tmp
  )
  (export "main" (func $main))

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

  (func $__nanyx_start
    call $main
    drop
  )

  (start $__nanyx_start)
)