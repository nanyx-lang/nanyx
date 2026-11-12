(module
  (func $choose__int (param $flag i32) (param $a_i64 i64) (param $b_i64 i64) (result i64)
    local.get $flag
    (if (result i64)
      (then
        local.get $a_i64
      )
      (else
        local.get $b_i64
      )
    )
  )

  (func $main (result i64)
    i32.const 1
    i64.const 10
    i64.const 20
    return_call $choose__int
  )
  (export "main" (func $main))
)