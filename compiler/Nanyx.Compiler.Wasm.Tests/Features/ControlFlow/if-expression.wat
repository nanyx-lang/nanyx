(module
  (func $choose (param $flag i32) (param $a i32) (param $b i32) (result i32)
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
  (export "choose" (func $choose))

  (func $main (result i32)
    i32.const 1
    i32.const 10
    i32.const 20
    return_call $choose
  )
  (export "main" (func $main))
)
