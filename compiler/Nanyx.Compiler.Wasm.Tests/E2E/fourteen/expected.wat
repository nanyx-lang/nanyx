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

  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (table $ctx_table 2 funcref)
  (elem (i32.const 0) $__lambda_1 $__lambda_3)

  (func $main (result i32) (local $__use_double_0 i32) (local $__use_triple_2 i32)
    i32.const 4
    call $__nanyx_alloc
    i32.const 0
    i32.const 0
    call $__nanyx_record_set
    local.set $__use_double_0
    i32.const 4
    call $__nanyx_alloc
    i32.const 1
    i32.const 0
    call $__nanyx_record_set
    local.set $__use_triple_2
    local.get $__use_double_0
    i32.const 5
    local.get $__use_double_0
    i32.load
    call_indirect (type $ctx_fn_2)
    local.get $__use_triple_2
    i32.const 10
    local.get $__use_triple_2
    i32.load
    call_indirect (type $ctx_fn_2)
    i32.add
  )
  (export "main" (func $main))

  (func $__lambda_1 (param $__env i32) (param $x i32) (result i32)
    local.get $x
    i32.const 2
    i32.mul
  )
  (export "__lambda_1" (func $__lambda_1))

  (func $__lambda_3 (param $__env i32) (param $x i32) (result i32)
    local.get $x
    i32.const 3
    i32.mul
  )
  (export "__lambda_3" (func $__lambda_3))
)
