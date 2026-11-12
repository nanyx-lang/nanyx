(module
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

  (func $main (result i64)
    i64.const 7
    return_call $g
  )
  (export "main" (func $main))
)