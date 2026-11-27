(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))

  (func $__nanyx_string_length (param $s i32) (result i32)
    local.get $s
    i32.const 4
    i32.sub
    i32.load
  )
  (func $__nanyx_write_stderr (param $s i32)
    i32.const 0
    local.get $s
    i32.store
    i32.const 4
    local.get $s
    call $__nanyx_string_length
    i32.store
    i32.const 2
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )
  (func $__nanyx_crashed (param $message i32)
    unreachable
  )
  (func $__nanyx_deferred)

  (data (i32.const 256) "\00\00\00\00R\00\00\0012:27: error NYX0201: Type mismatch: 'bool' vs 'int' (deferred from compile time)\0a\00\00")

  (func $inRange (param $x_i64 i64) (param $lo_i64 i64) (param $hi_i64 i64) (result i32)
    local.get $x_i64
    local.get $lo_i64
    i64.ge_s
    local.get $x_i64
    local.get $hi_i64
    i64.le_s
    i32.and
  )
  (export "inRange" (func $inRange))

  (func $check (param $a i32) (param $b i32) (result i64)
    local.get $a
    local.get $b
    i32.or
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )
  (export "check" (func $check))

  (func $main (result i64) (local $x__in1_i64 i64) (local $lo__in1_i64 i64) (local $hi__in1_i64 i64) (local $r1 i32) (local $a__in3 i32) (local $b__in3 i32) (local $c1_i64 i64) (local $a__in4 i32) (local $b__in4 i32) (local $c2_i64 i64)
    i64.const 5
    local.set $x__in1_i64
    i64.const 1
    local.set $lo__in1_i64
    i64.const 10
    local.set $hi__in1_i64
    local.get $x__in1_i64
    local.get $lo__in1_i64
    i64.ge_s
    local.get $x__in1_i64
    local.get $hi__in1_i64
    i64.le_s
    i32.and
    local.set $r1
    i32.const 1
    local.set $a__in3
    i32.const 0
    local.set $b__in3
    local.get $a__in3
    local.get $b__in3
    i32.or
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $c1_i64
    i32.const 0
    local.set $a__in4
    i32.const 0
    local.set $b__in4
    local.get $a__in4
    local.get $b__in4
    i32.or
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
    local.set $c2_i64
    local.get $r1
    local.get $c1_i64
    i64.const 1
    i64.eq
    i32.and
    (if (result i32)
      (then
        i32.const 264
    call $__nanyx_write_stderr
    call $__nanyx_deferred
    i32.const 264
    call $__nanyx_crashed
    unreachable
      )
      (else
        i32.const 0
      )
    )
    local.get $c2_i64
    i64.const 0
    i64.eq
    i32.and
    (if (result i64)
      (then
        i64.const 42
      )
      (else
        i64.const 0
      )
    )
  )
  (export "main" (func $main))
)
