(module
  (func $nested (param $x i32) (result i32) (local $y i32) (local $z i32)
    local.get $x
    i32.const 1
    i32.add
    local.set $y
    local.get $y
    i32.const 2
    i32.add
    local.set $z
    local.get $z
    i32.const 3
    i32.add
  )
  (export "nested" (func $nested))

  (func $main (result i32)
    i32.const 5
    return_call $nested
  )
  (export "main" (func $main))
)