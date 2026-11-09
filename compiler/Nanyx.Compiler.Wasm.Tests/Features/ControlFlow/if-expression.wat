(module
  (func $choose__int (param $flag i32) (param $a i32) (param $b i32) (result i32)
    local.get $flag
    (if (result i32)
      (then
        local.get $a
      )
      (else
        local.get $b
      )
    )
  )

  (func $main (result i32)
    i32.const 1
    i32.const 10
    i32.const 20
    return_call $choose__int
  )
  (export "main" (func $main))
)
