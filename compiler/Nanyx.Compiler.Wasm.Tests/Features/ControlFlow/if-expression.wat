(module
  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

  (func $choose__int (param $flag i32) (param $a_i64 i64) (param $b_i64 i64) (result i64)
    local.get $flag
    (if (result i64)
      (then
        local.get $a_i64
      )
      (else
        local.get $b_i64
      )
    )
  )

  (func $main__build (result i64) (local $flag__in1 i32) (local $a__in1_i64 i64) (local $b__in1_i64 i64)
    i32.const 1
    local.set $flag__in1
    i64.const 10
    local.set $a__in1_i64
    i64.const 20
    local.set $b__in1_i64
    local.get $flag__in1
    (if (result i64)
      (then
        local.get $a__in1_i64
      )
      (else
        local.get $b__in1_i64
      )
    )
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