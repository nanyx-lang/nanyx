(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
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

  (data (i32.const 256) "O\00\00\0012:27: error NYX0201: Type mismatch: bool vs int. (deferred from compile time)\0a\00")

  (func $inRange (param $x i32) (param $lo i32) (param $hi i32) (result i32)
    local.get $x
    local.get $lo
    i32.ge_s
    local.get $x
    local.get $hi
    i32.le_s
    i32.and
  )
  (export "inRange" (func $inRange))

  (func $check (param $a i32) (param $b i32) (result i32)
    local.get $a
    local.get $b
    i32.or
    (if (result i32)
      (then
        i32.const 1
      )
      (else
        i32.const 0
      )
    )
  )
  (export "check" (func $check))

  (func $main (result i32) (local $r1 i32) (local $r2 i32) (local $c1 i32) (local $c2 i32)
    i32.const 5
    i32.const 1
    i32.const 10
    call $inRange
    local.set $r1
    i32.const 15
    i32.const 1
    i32.const 10
    call $inRange
    local.set $r2
    i32.const 1
    i32.const 0
    call $check
    local.set $c1
    i32.const 0
    i32.const 0
    call $check
    local.set $c2
    local.get $r1
    local.get $c1
    i32.const 1
    i32.eq
    i32.and
    i32.const 260
    call $__nanyx_write_stderr
    i32.const 1
    call $proc_exit
    unreachable
    i32.and
    local.get $c2
    i32.const 0
    i32.eq
    i32.and
    (if (result i32)
      (then
        i32.const 42
      )
      (else
        i32.const 0
      )
    )
  )
  (export "main" (func $main))
)