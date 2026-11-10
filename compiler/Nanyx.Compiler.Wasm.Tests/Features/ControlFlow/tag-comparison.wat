(module
  (memory 1)
  (export "memory" (memory 0))

  (func $classify__tags_ (param $value i32) (result i32)
    local.get $value
    i32.const 3
    call $__nanyx_equals_tags_ok
    (if (result i32)
      (then
        i32.const 1
      )
      (else
        i32.const 0
      )
    )
  )

  (func $main (result i32)
    i32.const 3
    return_call $classify__tags_
  )
  (export "main" (func $main))

  (func $__nanyx_equals_tags_ok (param $a i32) (param $b i32) (result i32)
    ;; #ok
    local.get $a
    local.get $b
    i32.or
    i32.const 1
    i32.and
    if
      local.get $a
      local.get $b
      i32.eq
      return
    end
    local.get $a
    i32.load
    local.get $b
    i32.load
    i32.ne
    if
      i32.const 0
      return
    end
    i32.const 1
  )
)