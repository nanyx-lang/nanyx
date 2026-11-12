(module
  (func $isPositive (param $x_i64 i64) (result i32)
    local.get $x_i64
    i64.const 0
    i64.gt_s
  )
  (export "isPositive" (func $isPositive))

  (func $main (result i64) (local $a i32) (local $b i32) (local $positive i32)
    i32.const 1
    local.set $a
    i32.const 0
    local.set $b
    i64.const 5
    call $isPositive
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