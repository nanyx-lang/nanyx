(module
  (func $__nanyx_int_mul (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    local.get $a
    local.get $b
    i64.mul
    local.set $r
    local.get $a
    i64.eqz
    if (result i32)
      i32.const 0
    else
      local.get $r
      local.get $a
      i64.div_s
      local.get $b
      i64.ne
    end
    if
      i64.const -9223372036854775808
      i64.const -1
      i64.div_s
      drop
    end
    local.get $r
  )

  (func $double (param $x_i64 i64) (result i64)
    local.get $x_i64
    i64.const 2
    call $__nanyx_int_mul
  )
  (export "double" (func $double))

  (func $main (result i64)
    i64.const 5
    return_call $double
  )
  (export "main" (func $main))
)