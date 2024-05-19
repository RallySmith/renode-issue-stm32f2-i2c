*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart6


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}
    Create Log Tester         1

*** Test Cases ***
Should Run Test Case
    [Timeout]                 20 seconds
    Load Script
    Start Emulation
    Should Not Be In Log      i2c1: Unhandled write to offset 0x4. Unhandled bits: [11] when writing value 0x91E.
    Wait For Line On Uart     PASS:<Platform test done
    Wait For Line On Uart     EXIT:<done>
