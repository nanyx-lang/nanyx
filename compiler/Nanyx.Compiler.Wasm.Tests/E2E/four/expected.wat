(module
  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

  (func $classify (param $x_i64 i64) (result i64)
    local.get $x_i64
      i64.const 0
      i64.eq
    (if (result i64)
      (then
        i64.const 0
      )
      (else
        local.get $x_i64
      i64.const 1
      i64.eq
    (if (result i64)
      (then
        i64.const 10
      )
      (else
        local.get $x_i64
      i64.const 2
      i64.eq
    (if (result i64)
      (then
        i64.const 20
      )
      (else
        i32.const 1
    (if (result i64)
      (then
        i64.const 99
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
  (export "classify" (func $classify))

  (func $main__build (result i64) (local $x__in1_i64 i64)
    i64.const 2
    local.set $x__in1_i64
    local.get $x__in1_i64
      i64.const 0
      i64.eq
    (if (result i64)
      (then
        i64.const 0
      )
      (else
        local.get $x__in1_i64
      i64.const 1
      i64.eq
    (if (result i64)
      (then
        i64.const 10
      )
      (else
        local.get $x__in1_i64
      i64.const 2
      i64.eq
    (if (result i64)
      (then
        i64.const 20
      )
      (else
        i32.const 1
    (if (result i64)
      (then
        i64.const 99
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
