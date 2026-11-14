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

  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

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

  (func $main__build (result i64) (local $x__in1_i64 i64) (local $__inlined3_i64 i64)
    i64.const 5
    local.set $x__in1_i64
    local.get $x__in1_i64
    i64.const 1
    call $__nanyx_int_add
    local.set $__inlined3_i64
    local.get $__inlined3_i64
    i64.const 2
    call $__nanyx_int_mul
  )

  (func $main (result i64)
    global.get $main__built
    i32.eqz
    if
      call $main__build
      global.set $main__value
      i32.const 1
      global.set $main__built
    end
    global.get $main__value
  )
  (export "main" (func $main))
)