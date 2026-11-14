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

  (func $main (result i64) (local $__arg46 i32) (local $b0_i64 i64) (local $__arg47 i32) (local $b1_i64 i64) (local $__arg48 i32) (local $b2_i64 i64) (local $__arg49 i32) (local $b3_i64 i64) (local $__arg50 i32) (local $b4_i64 i64) (local $__arg51 i32) (local $b5_i64 i64) (local $__arg52 i32) (local $b6_i64 i64) (local $__arg53 i32) (local $b7_i64 i64) (local $__arg54 i32) (local $b8_i64 i64) (local $__arg55 i32) (local $b9_i64 i64) (local $__arg56 i32) (local $b10_i64 i64) (local $__arg57 i32) (local $b11_i64 i64)
    i64.const 5
    i64.const 10
    i64.lt_s
    local.set $__arg46
    local.get $__arg46
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b0_i64
    i64.const 10
    i64.const 5
    i64.lt_s
    local.set $__arg47
    local.get $__arg47
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b1_i64
    i64.const 5
    i64.const 10
    i64.gt_s
    local.set $__arg48
    local.get $__arg48
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b2_i64
    i64.const 10
    i64.const 5
    i64.gt_s
    local.set $__arg49
    local.get $__arg49
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b3_i64
    i64.const 5
    i64.const 5
    i64.le_s
    local.set $__arg50
    local.get $__arg50
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b4_i64
    i64.const 5
    i64.const 4
    i64.le_s
    local.set $__arg51
    local.get $__arg51
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b5_i64
    i64.const 5
    i64.const 5
    i64.ge_s
    local.set $__arg52
    local.get $__arg52
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b6_i64
    i64.const 4
    i64.const 5
    i64.ge_s
    local.set $__arg53
    local.get $__arg53
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b7_i64
    i64.const 5
    i64.const 5
    i64.eq
    local.set $__arg54
    local.get $__arg54
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b8_i64
    i64.const 5
    i64.const 6
    i64.eq
    local.set $__arg55
    local.get $__arg55
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b9_i64
    i64.const 5
    i64.const 6
    i64.ne
    local.set $__arg56
    local.get $__arg56
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $b10_i64
    i64.const 5
    i64.const 5
    i64.ne
    local.set $__arg57
    local.get $__arg57
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
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