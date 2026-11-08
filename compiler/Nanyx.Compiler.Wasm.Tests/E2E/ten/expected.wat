(module
  (memory 1)
  (export "memory" (memory 0))

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

  (func $main (result i32) (local $t i32)
    i32.const 16
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
    i32.const 30
    i32.const 12
    call $__nanyx_record_set
    i32.const 4
    i32.add
    local.set $t
    local.get $t
    i32.const 0
    i32.add
    i32.load
    local.get $t
    i32.const 4
    i32.add
    i32.load
    i32.add
    local.get $t
    i32.const 8
    i32.add
    i32.load
    i32.add
  )
  (export "main" (func $main))
)