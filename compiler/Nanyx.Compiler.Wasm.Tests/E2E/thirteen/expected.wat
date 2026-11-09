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

  (global $heap_ptr (mut i32) (i32.const 4096))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $ptr i32)
    global.get $heap_ptr
    local.set $ptr
    local.get $ptr
    local.get $size
    i32.const 3
    i32.add
    i32.const -4
    i32.and
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
  )
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
    local.get $base
  )

  (data (i32.const 256) "\05\00\00\00Alice\00")
  (data (i32.const 266) "\03\00\00\00Bob\00")

  (func $printPerson (param $p i32) (result i32) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__interp_src i32) (local $__interp_tmp i32) (local $__interp_start i32) (local $__interp_lo i32) (local $__interp_hi i32) (local $__interp_swap i32) (local $__interp_hole_0_0 i32) (local $__dbg_tmp i32)
    local.get $p
    i32.const 4
    i32.add
    i32.load
    local.tee $__dbg_tmp
    call $dbg_str
    local.get $__dbg_tmp
    drop
    local.get $p
    i32.const 0
    i32.add
    i32.load
    local.tee $__dbg_tmp
    call $dbg
    local.get $__dbg_tmp
    drop
    local.get $p
    i32.const 8
    i32.add
    i32.load
    local.tee $__dbg_tmp
    call $dbg
    local.get $__dbg_tmp
    drop
    i32.const 0
  )
  (export "printPerson" (func $printPerson))

  (func $main (result i32) (local $p i32) (local $p2 i32)
    i32.const 16
    call $__nanyx_alloc
    i32.const 1
    i32.const 0
    call $__nanyx_record_set
    i32.const 30
    i32.const 4
    call $__nanyx_record_set
    i32.const 260
    i32.const 8
    call $__nanyx_record_set
    i32.const 100
    i32.const 12
    call $__nanyx_record_set
    i32.const 4
    i32.add
    local.set $p
    local.get $p
    call $printPerson
    drop
    i32.const 16
    call $__nanyx_alloc
    i32.const 1
    i32.const 0
    call $__nanyx_record_set
    i32.const 25
    i32.const 4
    call $__nanyx_record_set
    i32.const 270
    i32.const 8
    call $__nanyx_record_set
    i32.const 80
    i32.const 12
    call $__nanyx_record_set
    i32.const 4
    i32.add
    local.set $p2
    local.get $p2
    return_call $printPerson
  )
  (export "main" (func $main))
)
