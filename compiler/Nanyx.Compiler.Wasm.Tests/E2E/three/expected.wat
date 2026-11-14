(module
  (func $isPositive (param $x_i64 i64) (result i32)
    local.get $x_i64
    i64.const 0
    i64.gt_s
  )
  (export "isPositive" (func $isPositive))

  (func $main (result i64) (local $positive i32)
    i64.const 5
    i64.const 0
    i64.gt_s
    local.set $positive
    local.get $positive
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )
  (export "main" (func $main))
)
