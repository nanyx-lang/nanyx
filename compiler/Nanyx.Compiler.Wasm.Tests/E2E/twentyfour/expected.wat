(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4096))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $ptr i32)
    global.get $heap_ptr
    local.set $ptr
    local.get $ptr
    local.get $size
    i32.const 3
    i32.add
    i32.const -4
    i32.and
    i32.add
    global.set $heap_ptr
    block $fits
      loop $grow
        global.get $heap_ptr
        memory.size
        i32.const 65536
        i32.mul
        i32.le_u
        br_if $fits
        i32.const 1
        memory.grow
        i32.const -1
        i32.eq
        if
          unreachable
        end
        br $grow
      end
    end
    local.get $ptr
  )
  (func $__nanyx_copy_bytes (param $dst i32) (param $src i32) (param $len i32)
    (local $i i32)
    block $done
      loop $copy
        local.get $i
        local.get $len
        i32.ge_s
        br_if $done
        local.get $dst
        local.get $i
        i32.add
        local.get $src
        local.get $i
        i32.add
        i32.load8_u
        i32.store8
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $copy
      end
    end
  )
  (func $__nanyx_string_length (param $s i32) (result i32)
    local.get $s
    i32.const 4
    i32.sub
    i32.load
  )
  (func $__nanyx_string_alloc (param $len i32) (result i32)
    (local $base i32)
    local.get $len
    i32.const 5
    i32.add
    call $__nanyx_alloc
    local.set $base
    local.get $base
    local.get $len
    i32.store
    local.get $base
    i32.const 4
    i32.add
    local.get $len
    i32.add
    i32.const 0
    i32.store8
    local.get $base
    i32.const 4
    i32.add
  )
  (func $__nanyx_string_byte_at (param $s i32) (param $i i32) (result i32)
    local.get $i
    i32.const 0
    i32.lt_s
    local.get $i
    local.get $s
    call $__nanyx_string_length
    i32.ge_s
    i32.or
    if (result i32)
      i32.const -1
    else
      local.get $s
      local.get $i
      i32.add
      i32.load8_u
    end
  )
  (func $__nanyx_string_slice (param $s i32) (param $start i32) (param $len i32) (result i32)
    (local $total i32)
    (local $out i32)
    local.get $s
    call $__nanyx_string_length
    local.set $total
    local.get $start
    i32.const 0
    i32.lt_s
    if
      i32.const 0
      local.set $start
    end
    local.get $start
    local.get $total
    i32.gt_s
    if
      local.get $total
      local.set $start
    end
    local.get $len
    i32.const 0
    i32.lt_s
    if
      i32.const 0
      local.set $len
    end
    local.get $len
    local.get $total
    local.get $start
    i32.sub
    i32.gt_s
    if
      local.get $total
      local.get $start
      i32.sub
      local.set $len
    end
    local.get $len
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $s
    local.get $start
    i32.add
    local.get $len
    call $__nanyx_copy_bytes
    local.get $out
  )
  (func $__nanyx_string_from_byte (param $byte i32) (result i32)
    (local $out i32)
    i32.const 1
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $byte
    i32.store8
    local.get $out
  )
  (func $__nanyx_string_concat (param $a i32) (param $b i32) (result i32)
    (local $lenA i32)
    (local $lenB i32)
    (local $out i32)
    local.get $a
    call $__nanyx_string_length
    local.set $lenA
    local.get $b
    call $__nanyx_string_length
    local.set $lenB
    local.get $lenA
    local.get $lenB
    i32.add
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $a
    local.get $lenA
    call $__nanyx_copy_bytes
    local.get $out
    local.get $lenA
    i32.add
    local.get $b
    local.get $lenB
    call $__nanyx_copy_bytes
    local.get $out
  )
  (func $__nanyx_write_stdout (param $s i32)
    i32.const 0
    local.get $s
    i32.store
    i32.const 4
    local.get $s
    call $__nanyx_string_length
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
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
  (func $__nanyx_read_stdin_byte (result i32)
    i32.const 0
    i32.const 12
    i32.store
    i32.const 4
    i32.const 1
    i32.store
    i32.const 0
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_read
    if (result i32)
      i32.const -1
    else
      i32.const 8
      i32.load
      if (result i32)
        i32.const 12
        i32.load8_u
      else
        i32.const -1
      end
    end
  )
  (func $__nanyx_tag_box (param $code i32) (param $payload i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_alloc
    local.tee $cell
    local.get $code
    i32.store
    local.get $cell
    local.get $payload
    i32.store offset=4
    local.get $cell
  )

  (data (i32.const 256) "\00\00\00\00\00")
  (data (i32.const 261) "\0d\00\00\00Hello, World!\00")

  (func $print (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stdout
    i32.const 0
  )
  (export "print" (func $print))

  (func $println (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stdout
    i32.const 0
    drop
    i32.const 10
    call $__nanyx_string_from_byte
    call $__nanyx_write_stdout
    i32.const 0
  )
  (export "println" (func $println))

  (func $eprint (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stderr
    i32.const 0
  )
  (export "eprint" (func $eprint))

  (func $eprintln (param $text i32) (result i32)
    local.get $text
    call $__nanyx_write_stderr
    i32.const 0
    drop
    i32.const 10
    call $__nanyx_string_from_byte
    call $__nanyx_write_stderr
    i32.const 0
  )
  (export "eprintln" (func $eprintln))

  (func $console_withoutCarriageReturn (param $line i32) (result i32) (local $length i32)
    local.get $line
    call $__nanyx_string_length
    local.set $length
    local.get $length
    i32.const 0
    i32.gt_s
    local.get $line
    local.get $length
    i32.const 1
    i32.sub
    call $__nanyx_string_byte_at
    i32.const 13
    i32.eq
    i32.and
    (if (result i32)
      (then
        local.get $line
    i32.const 0
    local.get $length
    i32.const 1
    i32.sub
    call $__nanyx_string_slice
      )
      (else
        local.get $line
      )
    )
  )
  (export "console.withoutCarriageReturn" (func $console_withoutCarriageReturn))

  (func $console_readLineAfter (param $acc i32) (result i32) (local $byte i32)
    call $__nanyx_read_stdin_byte
    local.set $byte
    local.get $byte
    i32.const 0
    i32.lt_s
    (if (result i32)
      (then
        local.get $acc
    call $__nanyx_string_length
    i32.const 0
    i32.eq
    (if (result i32)
      (then
        i32.const 5
    i32.const 3
    call $__nanyx_tag_box
      )
      (else
        i32.const 7
    local.get $acc
    call $__nanyx_tag_box
      )
    )
      )
      (else
        local.get $byte
    i32.const 10
    i32.eq
    (if (result i32)
      (then
        i32.const 7
    local.get $acc
    call $console_withoutCarriageReturn
    call $__nanyx_tag_box
      )
      (else
        local.get $acc
    local.get $byte
    call $__nanyx_string_from_byte
    call $__nanyx_string_concat
    return_call $console_readLineAfter
      )
    )
      )
    )
  )
  (export "console.readLineAfter" (func $console_readLineAfter))

  (func $readLine (param $_ i32) (result i32)
    i32.const 260
    return_call $console_readLineAfter
  )
  (export "readLine" (func $readLine))

  (func $console_readToEndAfter (param $acc i32) (result i32) (local $byte i32)
    call $__nanyx_read_stdin_byte
    local.set $byte
    local.get $byte
    i32.const 0
    i32.lt_s
    (if (result i32)
      (then
        local.get $acc
      )
      (else
        local.get $acc
    local.get $byte
    call $__nanyx_string_from_byte
    call $__nanyx_string_concat
    return_call $console_readToEndAfter
      )
    )
  )
  (export "console.readToEndAfter" (func $console_readToEndAfter))

  (func $readToEnd (param $_ i32) (result i32)
    i32.const 7
    i32.const 260
    call $console_readToEndAfter
    call $__nanyx_tag_box
  )
  (export "readToEnd" (func $readToEnd))

  (func $flush (param $_ i32) (result i32)
    i32.const 0
  )
  (export "flush" (func $flush))

  (func $main (result i32)
    i32.const 265
    return_call $println
  )
  (export "main" (func $main))

  (func $__nanyx_start
    call $main
    drop
  )

  (start $__nanyx_start)
)
