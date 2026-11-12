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

  (func $boolToInt (param $b i32) (result i64)
    local.get $b
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )
  (export "boolToInt" (func $boolToInt))

  (func $main (result i64) (local $b0_i64 i64) (local $b1_i64 i64) (local $b2_i64 i64) (local $b3_i64 i64) (local $b4_i64 i64) (local $b5_i64 i64) (local $b6_i64 i64) (local $b7_i64 i64) (local $b8_i64 i64) (local $b9_i64 i64) (local $b10_i64 i64) (local $b11_i64 i64)
    i64.const 5
    i64.const 10
    i64.lt_s
    call $boolToInt
    local.set $b0_i64
    i64.const 10
    i64.const 5
    i64.lt_s
    call $boolToInt
    local.set $b1_i64
    i64.const 5
    i64.const 10
    i64.gt_s
    call $boolToInt
    local.set $b2_i64
    i64.const 10
    i64.const 5
    i64.gt_s
    call $boolToInt
    local.set $b3_i64
    i64.const 5
    i64.const 5
    i64.le_s
    call $boolToInt
    local.set $b4_i64
    i64.const 5
    i64.const 4
    i64.le_s
    call $boolToInt
    local.set $b5_i64
    i64.const 5
    i64.const 5
    i64.ge_s
    call $boolToInt
    local.set $b6_i64
    i64.const 4
    i64.const 5
    i64.ge_s
    call $boolToInt
    local.set $b7_i64
    i64.const 5
    i64.const 5
    i64.eq
    call $boolToInt
    local.set $b8_i64
    i64.const 5
    i64.const 6
    i64.eq
    call $boolToInt
    local.set $b9_i64
    i64.const 5
    i64.const 6
    i64.ne
    call $boolToInt
    local.set $b10_i64
    i64.const 5
    i64.const 5
    i64.ne
    call $boolToInt
    local.set $b11_i64
    local.get $b0_i64
    local.get $b1_i64
    i64.const 2
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b2_i64
    i64.const 4
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b3_i64
    i64.const 8
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b4_i64
    i64.const 16
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b5_i64
    i64.const 32
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b6_i64
    i64.const 64
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b7_i64
    i64.const 128
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b8_i64
    i64.const 256
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b9_i64
    i64.const 512
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b10_i64
    i64.const 1024
    call $__nanyx_int_mul
    call $__nanyx_int_add
    local.get $b11_i64
    i64.const 2048
    call $__nanyx_int_mul
    call $__nanyx_int_add
  )
  (export "main" (func $main))
)