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
    i32.const 12
    call $__nanyx_alloc
    i32.const 1
    i32.const 0
    call $__nanyx_record_set
    i32.const 10
    i32.const 4
    call $__nanyx_record_set
    i32.const 20
    i32.const 8
    call $__nanyx_record_set
    i32.const 4
    i32.add
    local.set $args
    local.get $args
    i32.const 0
    i32.add
    i32.load
    local.get $args
    i32.const 4
    i32.add
    i32.load
    call $f
    local.set $r2
    local.get $r1
    local.get $r2
    i32.add
  )
  (export "main" (func $main))
)
