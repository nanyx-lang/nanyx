(module
  (func $threshold (param $x i32) (result i32)
    local.get $x
    i32.const 10
    i32.ge_s
    (if (result i32)
      (then
        i32.const 1
      )
      (else
        i32.const 0
      )
    )
  )
  (export "threshold" (func $threshold))

  (func $main (result i32)
    i32.const 15
    return_call $threshold
  )
  (export "main" (func $main))
)