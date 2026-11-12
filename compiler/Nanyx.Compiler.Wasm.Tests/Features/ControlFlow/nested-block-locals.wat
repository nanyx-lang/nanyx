(module
  (func $__nanyx_int_add (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    local.get $a
    local.get $b
    i64.add
    local.set $r
    local.get $a
    local.get $r
    i64.xor
    local.get $b
    local.get $r
    i64.xor
    i64.and
    i64.const 0
    i64.lt_s
    if
      i64.const -9223372036854775808
      i64.const -1
      i64.div_s
      drop
    end
    local.get $r
  )

  (func $nested (param $x_i64 i64) (result i64) (local $y_i64 i64) (local $z_i64 i64)
    local.get $x_i64
    i64.const 1
    call $__nanyx_int_add
    local.set $y_i64
    local.get $y_i64
    i64.const 2
    call $__nanyx_int_add
    local.set $z_i64
    local.get $z_i64
    i64.const 3
    call $__nanyx_int_add
  )
  (export "nested" (func $nested))

  (func $main (result i64)
    i64.const 5
    return_call $nested
  )
  (export "main" (func $main))
)