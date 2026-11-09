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
  (func $__nanyx_tag_box (param $code i32) (param $payload i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_alloc
    local.tee $cell
    local.get $code
    i32.store
    local.get $cell
    local.get $payload
    i32.store offset=4
    local.get $cell
  )
  (func $__nanyx_tag_equals (param $a i32) (param $b i32) (result i32)
    local.get $a
    local.get $b
    i32.eq
    if
      i32.const 1
      return
    end
    local.get $a
    local.get $b
    i32.or
    i32.const 1
    i32.and
    if
      i32.const 0
      return
    end
    local.get $a
    i32.load
    local.get $b
    i32.load
    i32.eq
    local.get $a
    i32.load offset=4
    local.get $b
    i32.load offset=4
    i32.eq
    i32.and
  )

  (func $classify (param $value i32) (result i32)
    local.get $value
    i32.const 3
    i32.const 3
    call $__nanyx_tag_box
    call $__nanyx_tag_equals
    (if (result i32)
      (then
        i32.const 1
      )
      (else
        i32.const 0
      )
    )
  )
  (export "classify" (func $classify))

  (func $main (result i32)
    i32.const 3
    i32.const 3
    call $__nanyx_tag_box
    return_call $classify
  )
  (export "main" (func $main))
)
