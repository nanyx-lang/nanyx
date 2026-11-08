(module
  (func $classify (param $x i32) (result i32) (local $__match_tmp_0 i32)
    local.get $x
    local.set $__match_tmp_0
    local.get $__match_tmp_0
      i32.const 0
      i32.eq
    (if (result i32)
      (then
        i32.const 0
      )
      (else
        local.get $__match_tmp_0
      i32.const 1
      i32.eq
    (if (result i32)
      (then
        i32.const 10
      )
      (else
        local.get $__match_tmp_0
      i32.const 2
      i32.eq
    (if (result i32)
      (then
        i32.const 20
      )
      (else
        i32.const 1
    (if (result i32)
      (then
        i32.const 99
      )
      (else
        unreachable
      )
    )
      )
    )
      )
    )
      )
    )
  )
  (export "classify" (func $classify))

  (func $main (result i32)
    i32.const 2
    return_call $classify
  )
  (export "main" (func $main))
)
