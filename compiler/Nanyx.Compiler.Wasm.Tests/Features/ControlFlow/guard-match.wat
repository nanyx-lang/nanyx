(module
  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

  (func $g (param $x_i64 i64) (result i64) (local $__match_tmp_0_i64 i64) (local $__guard_i64 i64)
    local.get $x_i64
    local.set $__match_tmp_0_i64
    local.get $__match_tmp_0_i64
      local.set $__guard_i64
      local.get $__guard_i64
    i64.const 5
    i64.gt_s
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i32.const 1
    (if (result i64)
      (then
        i64.const 0
      )
      (else
        unreachable
      )
    )
      )
    )
  )
  (export "g" (func $g))

  (func $main__build (result i64) (local $x__in1_i64 i64) (local $__match_tmp_0__in1_i64 i64) (local $__guard__in1_i64 i64)
    i64.const 7
    local.set $x__in1_i64
    local.get $x__in1_i64
    local.set $__match_tmp_0__in1_i64
    local.get $__match_tmp_0__in1_i64
      local.set $__guard__in1_i64
      local.get $__guard__in1_i64
    i64.const 5
    i64.gt_s
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i32.const 1
    (if (result i64)
      (then
        i64.const 0
      )
      (else
        unreachable
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