(module
  (memory 1)
  (export "memory" (memory 0))

  (func $__nanyx_free (param $ptr i32)
    (local $size i32)
    (local $head i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.set $size
    local.get $size
    i32.const 32
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4128
    end
    local.set $head
    local.get $ptr
    local.get $head
    i32.load
    i32.store
    local.get $head
    local.get $ptr
    i32.store
  )
  (func $__nanyx_release (param $count i32) (result i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    i32.eqz
    if
      i32.const 0
      return
    end
    local.get $n
    i32.const 1
    i32.eq
    if
      i32.const 1
      return
    end
    local.get $count
    local.get $n
    i32.const 1
    i32.sub
    i32.store
    i32.const 0
  )
  (func $__nanyx_retain (param $count i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    if
      local.get $count
      local.get $n
      i32.const 1
      i32.add
      i32.store
    end
  )
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

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_2_i32_i64_to_i64 (func (param i32 i64) (result i64)))
  (table $ctx_table 2 funcref)
  (elem (i32.const 0) $__lambda_1 $__lambda_3)

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 268) "\00\00\00\00\00\00\00\00\01\00\00\00")

  (func $main (result i64) (local $__use_double_0 i32) (local $__use_triple_2 i32)
    i32.const 264
    local.set $__use_double_0
    i32.const 276
    local.set $__use_triple_2
    local.get $__use_double_0
    i64.const 5
    local.get $__use_double_0
    i32.load
    call_indirect (type $ctx_fn_2_i32_i64_to_i64)
    local.get $__use_triple_2
    i64.const 10
    local.get $__use_triple_2
    i32.load
    call_indirect (type $ctx_fn_2_i32_i64_to_i64)
    call $__nanyx_int_add
  )
  (export "main" (func $main))

  (func $__lambda_1 (param $__env i32) (param $x_i64 i64) (result i64)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $x_i64
    i64.const 2
    call $__nanyx_int_mul
  )
  (export "__lambda_1" (func $__lambda_1))

  (func $__lambda_3 (param $__env i32) (param $x_i64 i64) (result i64)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $x_i64
    i64.const 3
    call $__nanyx_int_mul
  )
  (export "__lambda_3" (func $__lambda_3))

  (func $__nanyx_drop_fn_int_to_int (param $p i32)
    ;; (int -> int)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_int_to_int (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_0_word (param $p i32)
    ;; (0: word)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_0_word (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )
)