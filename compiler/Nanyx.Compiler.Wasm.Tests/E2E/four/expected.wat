(module
  (func $classify (param $x_i64 i64) (result i64) (local $__match_tmp_0_i64 i64)
    local.get $x_i64
    local.set $__match_tmp_0_i64
    local.get $__match_tmp_0_i64
      i64.const 0
      i64.eq
    (if (result i64)
      (then
        i64.const 0
      )
      (else
        local.get $__match_tmp_0_i64
      i64.const 1
      i64.eq
    (if (result i64)
      (then
        i64.const 10
      )
      (else
        local.get $__match_tmp_0_i64
      i64.const 2
      i64.eq
    (if (result i64)
      (then
        i64.const 20
      )
      (else
        i32.const 1
    (if (result i64)
      (then
        i64.const 99
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

  (func $main (result i64)
    i64.const 2
    return_call $classify
  )
  (export "main" (func $main))
)