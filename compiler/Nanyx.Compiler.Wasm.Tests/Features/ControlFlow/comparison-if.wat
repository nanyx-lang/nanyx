(module
  (func $threshold (param $x_i64 i64) (result i64)
    local.get $x_i64
    i64.const 10
    i64.ge_s
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )
  (export "threshold" (func $threshold))

  (func $main (result i64)
    i64.const 15
    return_call $threshold
  )
  (export "main" (func $main))
)