(module
  (memory 1)
  (export "memory" (memory 0))

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

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (table $ctx_table 0 funcref)

  (func $nested (param $x i32) (result i32) (local $y i32) (local $z i32)
    local.get $x
    i32.const 1
    i32.add
    local.set $y
    local.get $y
    i32.const 2
    i32.add
    local.set $z
    local.get $z
    i32.const 3
    i32.add
  )
  (export "nested" (func $nested))

  (func $main (result i32)
    i32.const 5
    return_call $nested
  )
  (export "main" (func $main))

  (func $__nanyx_drop_fn_unit_to_int (param $p i32)
    ;; (unit -> int)
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
)