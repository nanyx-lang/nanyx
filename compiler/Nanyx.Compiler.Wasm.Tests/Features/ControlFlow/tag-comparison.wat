(module
  (memory 1)
  (export "memory" (memory 0))

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
    return_call $classify
  )
  (export "main" (func $main))
)