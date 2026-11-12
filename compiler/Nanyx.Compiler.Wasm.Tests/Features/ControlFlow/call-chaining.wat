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

  (func $inc (param $x_i64 i64) (result i64)
    local.get $x_i64
    i64.const 1
    call $__nanyx_int_add
  )
  (export "inc" (func $inc))

  (func $double (param $x_i64 i64) (result i64)
    local.get $x_i64
    i64.const 2
    call $__nanyx_int_mul
  )
  (export "double" (func $double))

  (func $main (result i64)
    i64.const 5
    call $inc
    return_call $double
  )
  (export "main" (func $main))
)