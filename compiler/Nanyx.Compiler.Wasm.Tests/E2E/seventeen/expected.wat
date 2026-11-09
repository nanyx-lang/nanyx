(module
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
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
    local.get $base
  )

  (func $f (param $x i32) (param $y i32) (result i32)
    local.get $x
    local.get $y
    i32.add
  )
  (export "f" (func $f))

  (func $main (result i32) (local $r1 i32) (local $args i32) (local $r2 i32)
    i32.const 3
    i32.const 4
    call $f
    local.set $r1
    i32.const 8
    i32.const 1
    call $__nanyx_new_object
    i32.const 10
    i32.const 0
    call $__nanyx_record_set
    i32.const 20
    i32.const 4
    call $__nanyx_record_set
    local.set $args
    local.get $args
    i32.const 0
    i32.add
    i32.load
    local.get $args
    i32.const 4
    i32.add
    i32.load
    local.get $args
    call $__nanyx_drop_rec_0_int_1_int
    call $f
    local.set $r2
    local.get $r1
    local.get $r2
    i32.add
  )
  (export "main" (func $main))

  (func $__nanyx_drop_rec_0_int_1_int (param $p i32)
    ;; (0: int, 1: int)
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

  (func $__nanyx_dup_rec_0_int_1_int (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )
)