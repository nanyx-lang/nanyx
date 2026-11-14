(module
  (memory 1)
  (export "memory" (memory 0))

  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

  (func $classify__tags_ (param $value i32) (result i64)
    local.get $value
    i32.const 3
    call $__nanyx_equals_tags_ok
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )

  (func $main__build (result i64) (local $value__in1 i32)
    i32.const 3
    local.set $value__in1
    local.get $value__in1
    i32.const 3
    call $__nanyx_equals_tags_ok
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )

  (func $main (result i64)
    global.get $main__built
    i32.eqz
    if
      call $main__build
      global.set $main__value
      i32.const 1
      global.set $main__built
    end
    global.get $main__value
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