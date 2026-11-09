(module
  (func $g (param $x i32) (result i32) (local $__match_tmp_0 i32) (local $__guard i32)
    local.get $x
    local.set $__match_tmp_0
    local.get $__match_tmp_0
      local.set $__guard
      local.get $__guard
    i32.const 5
    i32.gt_s
    (if (result i32)
      (then
        i32.const 1
      )
      (else
        i32.const 1
    (if (result i32)
      (then
        i32.const 0
      )
      (else
        unreachable
      )
    )
      )
    )
  )
  (export "g" (func $g))

  (func $main (result i32)
    i32.const 7
    return_call $g
  )
  (export "main" (func $main))
)