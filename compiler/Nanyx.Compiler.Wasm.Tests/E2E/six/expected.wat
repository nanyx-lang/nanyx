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

  (func $isEven (param $n_i64 i64) (result i32)
    local.get $n_i64
    i64.const 2
    i64.rem_s
    i64.const 0
    i64.eq
  )
  (export "isEven" (func $isEven))

  (func $fizzbuzz (param $n_i64 i64) (result i64) (local $mod3_i64 i64) (local $mod5_i64 i64) (local $__match_tmp_0_i64 i64) (local $__match_tmp_1_i64 i64)
    local.get $n_i64
    i64.const 3
    i64.rem_s
    local.set $mod3_i64
    local.get $n_i64
    i64.const 5
    i64.rem_s
    local.set $mod5_i64
    local.get $mod3_i64
    local.set $__match_tmp_0_i64
    local.get $mod5_i64
    local.set $__match_tmp_1_i64
    local.get $__match_tmp_0_i64
      i64.const 0
      i64.eq
      local.get $__match_tmp_1_i64
      i64.const 0
      i64.eq
      i32.and
    (if (result i64)
      (then
        i64.const 15
      )
      (else
        local.get $__match_tmp_0_i64
      i64.const 0
      i64.eq
      i32.const 1
      i32.and
    (if (result i64)
      (then
        i64.const 3
      )
      (else
        i32.const 1
      local.get $__match_tmp_1_i64
      i64.const 0
      i64.eq
      i32.and
    (if (result i64)
      (then
        i64.const 5
      )
      (else
        i32.const 1
      i32.const 1
      i32.and
    (if (result i64)
      (then
        local.get $n_i64
      )
      (else
        unreachable
      )
    )
      )
    )
      )
    )
      )
    )
  )
  (export "fizzbuzz" (func $fizzbuzz))

  (func $main (result i64) (local $a_i64 i64) (local $b_i64 i64) (local $c_i64 i64) (local $d_i64 i64)
    i64.const 15
    call $fizzbuzz
    local.set $a_i64
    i64.const 9
    call $fizzbuzz
    local.set $b_i64
    i64.const 10
    call $fizzbuzz
    local.set $c_i64
    i64.const 7
    call $fizzbuzz
    local.set $d_i64
    local.get $a_i64
    local.get $b_i64
    call $__nanyx_int_add
    local.get $c_i64
    call $__nanyx_int_add
    local.get $d_i64
    call $__nanyx_int_add
  )
  (export "main" (func $main))
)